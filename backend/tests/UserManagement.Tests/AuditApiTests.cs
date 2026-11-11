using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Audit;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using Xunit;
namespace UserManagement.Tests;

// Exercise real authorization, strict query parsing and safe Mongo projections over HTTP.
public sealed class AuditApiTests : IAsyncLifetime
{
    private readonly TestApp app=new();
    public Task InitializeAsync()=>app.InitializeAsync();
    public Task DisposeAsync()=>app.DisposeAsync();
    private async Task<HttpClient> Admin()
    {
        var user=await app.SeedAsync("Admin");var client=app.CreateClient();var response=await client.PostAsJsonAsync("/api/login",new { username=user.Username,password="Test-password-123!" });Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString());return client;
    }
    private static async Task<JsonElement> Page(HttpClient client,string query="")
    {
        var response=await client.GetAsync("/api/logs"+query);Assert.Equal(HttpStatusCode.OK,response.StatusCode);return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
    [Fact]
    public async Task Admin_default_bounded_newest_order_safe_projection_optional_identity_and_no_read_events()
    {
        using var client=await Admin();await app.Mongo.Logs.DeleteManyAsync(Builders<LogDocument>.Filter.Empty);
        var now=DateTime.UtcNow;var identity=ObjectId.GenerateNewId();var logs=Enumerable.Range(0,25).Select(i=>new LogDocument { Timestamp=now.AddSeconds(i/5),Action="login",Status=i%2==0?"success":"failure",UserId=i%2==0?identity:null }).ToArray();
        var raw=app.Mongo.Database.GetCollection<BsonDocument>("logs");foreach(var log in logs.Reverse())await raw.InsertOneAsync(log.ToBsonDocument().Add("password_hash","never-return").Add("address","private-value").Add("metadata",new BsonDocument("token","secret-token")));
        var first=await Page(client);Assert.Equal(new[] { "items","limit","skip","total" },first.EnumerateObject().Select(x=>x.Name).OrderBy(x=>x));Assert.Equal(20,first.GetProperty("limit").GetInt32());Assert.Equal(0,first.GetProperty("skip").GetInt32());Assert.Equal(25,first.GetProperty("total").GetInt32());Assert.Equal(20,first.GetProperty("items").GetArrayLength());
        var second=await Page(client,"?limit=20&skip=20");var expected=logs.OrderByDescending(x=>x.Timestamp).ThenByDescending(x=>x.Id).Select(x=>x.Id.ToString());Assert.Equal(expected,first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(x=>x.GetProperty("id").GetString()));
        foreach(var item in first.GetProperty("items").EnumerateArray()) { Assert.Equal(item.TryGetProperty("user_id",out _)?new[] { "action","id","status","timestamp","user_id" }:new[] { "action","id","status","timestamp" },item.EnumerateObject().Select(x=>x.Name).OrderBy(x=>x));Assert.Matches("^[0-9a-f]{24}$",item.GetProperty("id").GetString()!);Assert.EndsWith("Z",item.GetProperty("timestamp").GetString()); }
        var text=first.GetRawText();foreach(var secret in new[] { "never-return","private-value","secret-token","password_hash","metadata" })Assert.DoesNotContain(secret,text);
        Assert.Equal(25,await raw.CountDocumentsAsync(new BsonDocument()));Assert.Equal(25,(await Page(client,"?limit=100")).GetProperty("items").GetArrayLength());Assert.Single((await Page(client,"?limit=1")).GetProperty("items").EnumerateArray());Assert.Equal(0,(await Page(client,"?skip=2147483647")).GetProperty("items").GetArrayLength());
        await raw.DeleteManyAsync(new BsonDocument());var empty=await Page(client);Assert.Equal(0,empty.GetProperty("total").GetInt32());Assert.Equal(0,empty.GetProperty("items").GetArrayLength());
    }
    public static IEnumerable<object[]> InvalidQueries()
    {
        foreach(var key in new[] { "limit","skip" })foreach(var value in new[] { "", "-1", "1.1", "+1", "true", "null", "[]", "{}", "2147483648", " 20 " })yield return [$"?{key}={Uri.EscapeDataString(value)}"];
        yield return ["?limit=0"];yield return ["?limit=101"];
        foreach(var key in new[] { "action","status","user_id","sort","direction","Limit","unknown","$where","limit[$ne]" })yield return [$"?{Uri.EscapeDataString(key)}=private-value"];
        yield return ["?limit=20&limit=20"];yield return ["?skip=0&skip=1"];yield return ["?%6cimit=20&limit=20"];yield return ["?limit=20&LIMIT=20"];
    }
    [Theory] [MemberData(nameof(InvalidQueries))]
    public async Task Invalid_unknown_duplicate_and_wrong_type_queries_return_safe_400_without_audit(string query)
    {
        using var client=await Admin();var before=await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty);var response=await client.GetAsync("/api/logs"+query);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;Assert.Equal("validation_error",body.GetProperty("code").GetString());Assert.Equal(400,body.GetProperty("status").GetInt32());Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));Assert.DoesNotContain("private-value",body.GetRawText());Assert.Equal(before,await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }
    [Theory] [InlineData("anonymous",401)] [InlineData("User",403)] [InlineData("expired",401)] [InlineData("invalid",401)] [InlineData("inactive",401)]
    public async Task Unauthorized_callers_are_denied_before_query_validation_and_no_events_written(string scenario,int status)
    {
        using var client=app.CreateClient();
        if(scenario!="anonymous") { var user=await app.SeedAsync(scenario=="User"?"User":"Admin");var token=app.Services.GetRequiredService<TokenService>().Issue(user).AccessToken;
            if(scenario=="expired") { var now=DateTime.UtcNow;token=new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("umm-tests","umm-test-browser",[new Claim("sub",user.Id.ToString()),new Claim("role",user.Role)],now.AddHours(-1),now.AddMinutes(-1),new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(app.Secret)),SecurityAlgorithms.HmacSha256))); }
            if(scenario=="invalid") token="not-a-token";if(scenario=="inactive")await app.Mongo.Users.UpdateOneAsync(x=>x.Id==user.Id,Builders<UserManagement.Api.Users.UserDocument>.Update.Set(x=>x.IsActive,false));client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        }
        foreach(var path in new[] { "/api/logs","/api/logs?unknown=x" }) { var response=await client.GetAsync(path);Assert.Equal((HttpStatusCode)status,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.Contains(status==403?"forbidden":"invalid_token",text);Assert.DoesNotContain("items",text); }
        Assert.Equal(0,await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }
    [Fact]
    public async Task Unreachable_real_audit_repository_returns_safe_503_not_empty_success()
    {
        using var admin=await Admin();var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"]="mongodb://127.0.0.1:1",["Mongo:Database"]=app.DatabaseName }).Build();
        using var unavailable=app.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton(new AuditService(new MongoLogRepository(new MongoContext(config))))));using var client=unavailable.CreateClient();client.DefaultRequestHeaders.Authorization=admin.DefaultRequestHeaders.Authorization;
        var response=await client.GetAsync("/api/logs");Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;Assert.Equal("database_unavailable",body.GetProperty("code").GetString());Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));Assert.DoesNotContain("mongodb",body.GetRawText());Assert.False(body.TryGetProperty("items",out _));
    }
}
