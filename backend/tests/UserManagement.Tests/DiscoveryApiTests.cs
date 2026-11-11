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
using MongoDB.Driver;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using UserManagement.Api.Discovery;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Verify strict GET query binding, safe page shape and persisted Admin authorization against real Mongo.
public sealed class DiscoveryApiTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();
    private async Task<HttpClient> AdminAsync()
    {
        var admin=await app.SeedAsync("Admin"); var client=app.CreateClient();
        var login=await client.PostAsJsonAsync("/api/login",new { username=admin.Username,password="Test-password-123!" });
        Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        var session=JsonSerializer.Deserialize<LoginResponse>(await login.Content.ReadAsStringAsync(),TestApp.Json)!;
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.AccessToken); return client;
    }
    private static async Task<UserListResponse> Page(HttpClient client,string query)
    {
        var response=await client.GetAsync("/api/users"+query); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        return JsonSerializer.Deserialize<UserListResponse>(await response.Content.ReadAsStringAsync(),TestApp.Json)!;
    }
    [Fact]
    public async Task Admin_defaults_return_bounded_safe_snake_case_page_and_no_read_audit()
    {
        using var client=await AdminAsync();
        await app.Mongo.Users.InsertManyAsync(Enumerable.Range(0,25).Select(i=>new UserDocument { Username=$"account-{i:00}",PasswordHash="never-return" }));
        var logs=await app.Mongo.Logs.CountDocumentsAsync(Builders<UserManagement.Api.Audit.LogDocument>.Filter.Empty);
        var response=await client.GetAsync("/api/users"); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var text=await response.Content.ReadAsStringAsync(); var body=JsonDocument.Parse(text).RootElement;
        Assert.Equal(new[] { "items","limit","skip","total" },body.EnumerateObject().Select(x=>x.Name).OrderBy(x=>x));
        Assert.Equal(20,body.GetProperty("limit").GetInt32()); Assert.Equal(0,body.GetProperty("skip").GetInt32()); Assert.Equal(26,body.GetProperty("total").GetInt32()); Assert.Equal(20,body.GetProperty("items").GetArrayLength());
        Assert.Equal(new[] { "address","city","country","created_at","education","id","is_active","role","updated_at","username" },body.GetProperty("items")[0].EnumerateObject().Select(x=>x.Name).OrderBy(x=>x));
        Assert.DoesNotContain("password",text); Assert.DoesNotContain("never-return",text); Assert.DoesNotContain("_id",text);
        Assert.Matches("^[0-9a-f]{24}$",body.GetProperty("items")[0].GetProperty("id").GetString()!);
        Assert.Equal(logs,await app.Mongo.Logs.CountDocumentsAsync(Builders<UserManagement.Api.Audit.LogDocument>.Filter.Empty));
    }
    [Theory]
    [MemberData(nameof(DiscoveryTests.Sorts),MemberType=typeof(DiscoveryTests))]
    public async Task Every_field_direction_and_combined_filter_is_observed_at_HTTP_boundary(string field,string direction)
    {
        using var client=await AdminAsync();
        var users=new[] {
            new UserDocument { Username="alpha",City="Find A",Country="UK",Education="Degree",IsActive=true },
            new UserDocument { Username="bravo",City="Find B",Country="UK",Education="Degree",IsActive=true },
            new UserDocument { Username="charlie",City="Find B",Country="UK",Education="Degree",IsActive=false },
            new UserDocument { Username="delta",City="Find D",Country="uk",Education="Degree",IsActive=true },
            new UserDocument { Username="echo",Country="",Education="" }
        };
        await app.Mongo.Users.InsertManyAsync(users.Reverse());
        string? Value(UserDocument u)=>field switch { "username"=>u.Username,"city"=>u.City,"country"=>u.Country,_=>u.Education };
        var expected=direction=="asc"?users.Take(2).OrderBy(Value,StringComparer.Ordinal).ThenBy(x=>x.Id):users.Take(2).OrderByDescending(Value,StringComparer.Ordinal).ThenBy(x=>x.Id);
        var query=$"?search=FIND&country=UK&education=Degree&is_active=true&sort={field}&direction={direction}&limit=1";
        var first=await Page(client,query);var second=await Page(client,query+"&skip=1");
        Assert.Equal(expected.Select(x=>x.Id.ToString()),first.Items.Concat(second.Items).Select(x=>x.Id));Assert.Equal(2,first.Total);Assert.Equal(2,second.Total);Assert.Equal(1,second.Skip);
        Assert.Equal("charlie",Assert.Single((await Page(client,"?search=find&is_active=false")).Items).Username);
        Assert.Equal("delta",Assert.Single((await Page(client,"?country=uk")).Items).Username);
        Assert.Equal("echo",Assert.Single((await Page(client,"?country=&education=")).Items).Username);
        Assert.Equal(3,(await Page(client,"?country=UK")).Total);Assert.Equal(4,(await Page(client,"?education=Degree")).Total);
    }
    public static IEnumerable<object[]> InvalidParameters()
    {
        foreach(var key in new[] { "limit","skip" }) foreach(var value in new[] { "", "-1", "1.1", "+1", "true", "null", "[]", "2147483648", " 20 " }) yield return [$"?{key}={Uri.EscapeDataString(value)}"];
        yield return ["?limit=0"]; yield return ["?limit=101"];
        foreach(var value in new[] { "", "True", "1", "0", "null", "{}" }) yield return ["?is_active="+Uri.EscapeDataString(value)];
        foreach(var value in new[] { "", "id", "role", "$where", "Username" }) yield return ["?sort="+Uri.EscapeDataString(value)];
        foreach(var value in new[] { "", "ASC", "descending", "sideways" }) yield return ["?direction="+value];
        foreach(var key in new[] { "search","country","education" }) yield return [$"?{key}="+new string('x',101)];
        foreach(var key in new[] { "role","id","password_hash","isAdmin","$where","country[$ne]","search[$regex]","Limit","unknown" }) yield return [$"?{Uri.EscapeDataString(key)}=unapproved"];
        foreach(var key in new[] { "limit","skip","search","country","education","is_active","sort","direction" }) yield return [$"?{key}=1&{key}=1"];
        yield return ["?country=UK&COUNTRY=UK"]; yield return ["?%6cimit=20&limit=20"];
    }
    [Theory]
    [MemberData(nameof(InvalidParameters))]
    public async Task Invalid_unknown_duplicate_mass_query_and_operator_keys_return_safe_400(string query)
    {
        using var client=await AdminAsync();var before=await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty);
        var response=await client.GetAsync("/api/users"+query);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        var text=await response.Content.ReadAsStringAsync();var body=JsonDocument.Parse(text).RootElement;
        Assert.Equal(400,body.GetProperty("status").GetInt32());Assert.Equal("validation_error",body.GetProperty("code").GetString());Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("unapproved",text);Assert.DoesNotContain("mongodb",text);Assert.Equal(before,await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }
    [Theory]
    [InlineData(".*")] [InlineData("[a]+$")] [InlineData("{\"$ne\":null}")] [InlineData("$where")]
    public async Task Injection_text_is_literal_for_search_and_exact_filters(string literal)
    {
        using var client=await AdminAsync(); await app.Mongo.Users.InsertManyAsync(new[] { new UserDocument { Username="literal",City=literal,Country=literal,Education=literal },new UserDocument { Username="other" } });
        var encoded=Uri.EscapeDataString(literal);var page=await Page(client,$"?search={encoded}&country={encoded}&education={encoded}");Assert.Equal("literal",Assert.Single(page.Items).Username);Assert.Equal(1,page.Total);
    }
    [Fact]
    public async Task Bounds_nulls_empty_and_beyond_end_are_200_with_accurate_total()
    {
        using var client=await AdminAsync();
        await app.Mongo.Users.InsertOneAsync(new UserDocument { Username="one",City=null,Country=null,Education=null });
        var maximum=await Page(client,"?limit=100&skip=0");Assert.Equal(2,maximum.Total);Assert.Equal(2,maximum.Items.Count);Assert.Null(maximum.Items.Single(x=>x.Username=="one").City);
        var minimum=await Page(client,"?limit=1");Assert.Single(minimum.Items);Assert.Equal(2,minimum.Total);
        var beyond=await Page(client,"?skip=2147483647");Assert.Empty(beyond.Items);Assert.Equal(2,beyond.Total);
        Assert.Equal(0,(await Page(client,"?search=absent")).Total);
        foreach(var field in new[] { "search","country","education" }) Assert.Equal(0,(await Page(client,$"?{field}="+new string('x',100))).Total);
        Assert.Equal(2,(await Page(client,"?search=")).Total);
    }
    [Theory]
    [InlineData("anonymous",401)] [InlineData("User",403)] [InlineData("invalid",401)] [InlineData("expired",401)] [InlineData("tampered",401)] [InlineData("wrong_issuer",401)] [InlineData("wrong_audience",401)] [InlineData("inactive",401)] [InlineData("deleted",401)] [InlineData("role_changed",401)]
    public async Task Anonymous_User_and_invalid_or_stale_tokens_cannot_discover_even_with_invalid_queries(string scenario,int status)
    {
        using var client=app.CreateClient();
        if(scenario!="anonymous") {
            var user=await app.SeedAsync(scenario=="User"?"User":"Admin");var token=app.Services.GetRequiredService<TokenService>().Issue(user).AccessToken;
            if(scenario is "expired" or "wrong_issuer" or "wrong_audience") {
                var now=DateTime.UtcNow;token=new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(scenario=="wrong_issuer"?"wrong":"umm-tests",scenario=="wrong_audience"?"wrong":"umm-test-browser",[new Claim("sub",user.Id.ToString()),new Claim("role","Admin")],now.AddHours(-1),scenario=="expired"?now.AddMinutes(-1):now.AddMinutes(30),new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(app.Secret)),SecurityAlgorithms.HmacSha256)));
            }
            if(scenario=="invalid")token="not-a-token";
            if(scenario=="tampered") { var parts=token.Split('.');parts[1]=Base64UrlEncoder.Encode("{\"sub\":\"forged\",\"role\":\"Admin\"}");token=string.Join('.',parts); }
            if(scenario=="inactive")await app.Mongo.Users.UpdateOneAsync(x=>x.Id==user.Id,Builders<UserDocument>.Update.Set(x=>x.IsActive,false));
            if(scenario=="deleted")await app.Mongo.Users.DeleteOneAsync(x=>x.Id==user.Id);
            if(scenario=="role_changed")await app.Mongo.Users.UpdateOneAsync(x=>x.Id==user.Id,Builders<UserDocument>.Update.Set(x=>x.Role,"User"));
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        }
        foreach(var path in new[] { "/api/users","/api/users?role=Admin" }) {
            var response=await client.GetAsync(path);Assert.Equal((HttpStatusCode)status,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.Contains(status==403?"forbidden":"invalid_token",text);Assert.DoesNotContain("items",text);
        }
    }
    [Fact]
    public async Task Real_unreachable_discovery_repository_returns_safe_503_not_fabricated_empty_data()
    {
        using var adminClient=await AdminAsync();
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"]="mongodb://127.0.0.1:1",["Mongo:Database"]=app.DatabaseName }).Build();
        using var unavailable=app.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton(new DiscoveryService(new MongoUserRepository(new MongoContext(config))))));
        using var client=unavailable.CreateClient();client.DefaultRequestHeaders.Authorization=adminClient.DefaultRequestHeaders.Authorization;
        var response=await client.GetAsync("/api/users");Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.Contains("database_unavailable",text);Assert.Contains("traceId",text);Assert.DoesNotContain("mongodb",text);Assert.DoesNotContain("items",text);
    }
}
