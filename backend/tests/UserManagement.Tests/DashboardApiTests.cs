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
using UserManagement.Api.Dashboard;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Real HTTP authorization and aggregate projection, with real Mongo and an injected clock.
public sealed class DashboardApiTests : IAsyncLifetime
{
    private readonly TestApp app=new();
    public Task InitializeAsync()=>app.InitializeAsync();
    public Task DisposeAsync()=>app.DisposeAsync();
    private async Task<HttpClient> Admin()
    {
        var user=await app.SeedAsync("Admin");var client=app.CreateClient();
        var response=await client.PostAsJsonAsync("/api/login",new { username=user.Username,password="Test-password-123!" });Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString());return client;
    }
    [Fact]
    public async Task Admin_summary_is_exact_safe_snake_case_UTC_query_free_and_read_only()
    {
        using var admin=await Admin();var clock=new DashboardClock();
        using var host=app.WithWebHostBuilder(b=>b.ConfigureServices(s=>s.AddSingleton<TimeProvider>(clock)));using var client=host.CreateClient();client.DefaultRequestHeaders.Authorization=admin.DefaultRequestHeaders.Authorization;
        await app.Mongo.Logs.DeleteManyAsync(Builders<LogDocument>.Filter.Empty);
        var raw=app.Mongo.Database.GetCollection<BsonDocument>("users");
        await raw.InsertOneAsync(new UserDocument { Username="safe-projection",Country="India",IsActive=false }.ToBsonDocument().Add("sensitive","private-value"));
        var start=clock.Now.UtcDateTime.AddDays(-7);var end=clock.Now.UtcDateTime;
        await app.Mongo.Database.GetCollection<BsonDocument>("logs").InsertManyAsync(new[] {
            new LogDocument { Timestamp=start,Action="login",Status="success" }.ToBsonDocument().Add("token","private-token"),
            new LogDocument { Timestamp=end,Action="login",Status="failure" }.ToBsonDocument(),
            new LogDocument { Timestamp=end.AddMilliseconds(1),Action="login",Status="success" }.ToBsonDocument(),
            new LogDocument { Timestamp=start.AddMilliseconds(-1),Action="login",Status="failure" }.ToBsonDocument(),
            new LogDocument { Timestamp=end,Action="create_user",Status="success" }.ToBsonDocument()
        });
        var response=await client.GetAsync("/api/dashboard");Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("application/json",response.Content.Headers.ContentType?.MediaType);
        var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(new[] { "active","activity","country_distribution","generated_at","inactive","login_failure","login_success","total","window_start" },body.EnumerateObject().Select(x=>x.Name).OrderBy(x=>x));
        Assert.Equal(2,body.GetProperty("total").GetInt64());Assert.Equal(1,body.GetProperty("active").GetInt64());Assert.Equal(1,body.GetProperty("inactive").GetInt64());Assert.Equal(3,body.GetProperty("activity").GetInt64());Assert.Equal(1,body.GetProperty("login_success").GetInt64());Assert.Equal(1,body.GetProperty("login_failure").GetInt64());
        var countries=body.GetProperty("country_distribution").EnumerateArray().ToArray();Assert.Equal(new[] { "India","Unknown" },countries.Select(x=>x.GetProperty("country").GetString()));
        Assert.All(countries,x=> { Assert.Equal(1,x.GetProperty("count").GetInt64());Assert.Equal(new[] { "count","country" },x.EnumerateObject().Select(p=>p.Name).OrderBy(n=>n)); });
        Assert.Equal(end,body.GetProperty("generated_at").GetDateTime());Assert.Equal(start,body.GetProperty("window_start").GetDateTime());Assert.EndsWith("Z",body.GetProperty("generated_at").GetString());Assert.EndsWith("Z",body.GetProperty("window_start").GetString());Assert.Equal(1,clock.Reads);
        foreach(var secret in new[] { "password_hash","username","user_id","private-value","private-token","sensitive","token" })Assert.DoesNotContain(secret,body.GetRawText());
        var again=await client.GetAsync("/api/dashboard");Assert.Equal(await response.Content.ReadAsStringAsync(),await again.Content.ReadAsStringAsync());Assert.Equal(5,await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
        // Keep the authorized account but clear logs: true empty activity, not a mocked response.
        await app.Mongo.Logs.DeleteManyAsync(Builders<LogDocument>.Filter.Empty);var empty=await client.GetFromJsonAsync<DashboardSummary>("/api/dashboard",TestApp.Json);Assert.Equal(0,empty!.Activity);Assert.Equal(0,empty.LoginSuccess);Assert.Equal(0,empty.LoginFailure);
    }
    [Theory] [InlineData("?unknown=private-value")] [InlineData("?limit=20")] [InlineData("?window_start=2026-01-01")] [InlineData("?country=India")] [InlineData("?%24where=private-value")] [InlineData("?unknown=")] [InlineData("?unknown=x&unknown=y")]
    public async Task Any_query_is_rejected_with_safe_400_and_no_summary_or_event(string query)
    {
        using var client=await Admin();var before=await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty);
        var response=await client.GetAsync("/api/dashboard"+query);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;Assert.Equal(400,body.GetProperty("status").GetInt32());Assert.Equal("validation_error",body.GetProperty("code").GetString());Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));Assert.False(body.TryGetProperty("total",out _));Assert.DoesNotContain("private-value",body.GetRawText());Assert.Equal(before,await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }
    [Theory] [InlineData("anonymous",401)] [InlineData("User",403)] [InlineData("expired",401)] [InlineData("invalid",401)] [InlineData("inactive",401)] [InlineData("deleted",401)] [InlineData("changed_role",401)]
    public async Task Denials_precede_query_validation_and_do_not_expose_aggregate_counts(string scenario,int status)
    {
        using var client=app.CreateClient();
        if(scenario!="anonymous")
        {
            var user=await app.SeedAsync(scenario=="User"?"User":"Admin");var token=app.Services.GetRequiredService<TokenService>().Issue(user).AccessToken;
            if(scenario=="expired") { var now=DateTime.UtcNow;token=new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("umm-tests","umm-test-browser",[new Claim("sub",user.Id.ToString()),new Claim("role",user.Role)],now.AddHours(-1),now.AddMinutes(-1),new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(app.Secret)),SecurityAlgorithms.HmacSha256))); }
            if(scenario=="invalid")token="not-a-token";
            if(scenario=="inactive")await app.Mongo.Users.UpdateOneAsync(x=>x.Id==user.Id,Builders<UserDocument>.Update.Set(x=>x.IsActive,false));
            if(scenario=="deleted")await app.Mongo.Users.DeleteOneAsync(x=>x.Id==user.Id);
            if(scenario=="changed_role")await app.Mongo.Users.UpdateOneAsync(x=>x.Id==user.Id,Builders<UserDocument>.Update.Set(x=>x.Role,"User"));
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        }
        foreach(var path in new[] { "/api/dashboard","/api/dashboard?unknown=x" })
        {
            var response=await client.GetAsync(path);Assert.Equal((HttpStatusCode)status,response.StatusCode);var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;Assert.Equal(status,body.GetProperty("status").GetInt32());Assert.Equal(status==403?"forbidden":"invalid_token",body.GetProperty("code").GetString());Assert.False(body.TryGetProperty("total",out _));
        }
        Assert.Equal(0,await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }
    [Theory] [InlineData("users")] [InlineData("logs")]
    public async Task Unavailable_real_summary_storage_returns_safe_503_not_partial_success(string collection)
    {
        using var admin=await Admin();var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"]="mongodb://127.0.0.1:1",["Mongo:Database"]=app.DatabaseName }).Build();var unreachable=new MongoContext(config);
        var service=new DashboardService(collection=="users"?new MongoUserRepository(unreachable):app.Services.GetRequiredService<IUserRepository>(),collection=="logs"?new MongoLogRepository(unreachable):app.Services.GetRequiredService<MongoLogRepository>(),new DashboardClock());
        using var host=app.WithWebHostBuilder(b=>b.ConfigureServices(s=>s.AddSingleton(service)));using var client=host.CreateClient();client.DefaultRequestHeaders.Authorization=admin.DefaultRequestHeaders.Authorization;
        var response=await client.GetAsync("/api/dashboard");Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal("application/problem+json",response.Content.Headers.ContentType?.MediaType);
        var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;Assert.Equal(503,body.GetProperty("status").GetInt32());Assert.Equal("database_unavailable",body.GetProperty("code").GetString());Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));Assert.DoesNotContain("mongodb",body.GetRawText());Assert.False(body.TryGetProperty("total",out _));Assert.False(body.TryGetProperty("generated_at",out _));
    }
}
