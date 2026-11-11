using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using UserManagement.Api.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using UserManagement.Api.Auth;
using Xunit;
namespace UserManagement.Tests;

// Exercise actual HTTP validation, JSON privacy and JWT middleware against real Mongo.
public sealed class AuthenticationApiTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Login_returns_safe_snake_case_response_and_health_reports_real_readiness()
    {
        var user = await app.SeedAsync("Admin"); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new { username=user.Username, password="Test-password-123!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", text); Assert.DoesNotContain(user.PasswordHash, text); Assert.DoesNotContain("_id", text);
        var result = JsonSerializer.Deserialize<LoginResponse>(text, TestApp.Json)!;
        Assert.Equal(user.Id.ToString(), result.User.Id); Assert.Equal("Bearer", result.TokenType);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth-probe/admin")).StatusCode);
        var health = await client.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.OK, health.StatusCode); Assert.Contains("healthy", await health.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_health_returns_safe_503_problem_when_real_mongo_ping_is_unreachable()
    {
        // Reserve a loopback port without listening so no Mongo server can answer it.
        using var unavailableEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailableEndpoint.ExclusiveAddressUse = true;
        unavailableEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)unavailableEndpoint.LocalEndPoint!;
        var uri = $"mongodb://127.0.0.1:{endpoint.Port}";
        var outageContext = new MongoContext(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = uri, ["Mongo:Database"] = app.DatabaseName
        }).Build());

        // Replace only controller activation; startup/index creation still use the real fixture database.
        using var outageApp = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddControllersAsServices();
            services.AddTransient<HealthController>(_ => new HealthController(outageContext));
        }));
        using var client = outageApp.CreateClient();
        var startupContext = outageApp.Services.GetRequiredService<MongoContext>();
        Assert.NotSame(outageContext, startupContext);
        await startupContext.PingAsync(CancellationToken.None);
        Assert.Null(client.DefaultRequestHeaders.Authorization);

        using var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        var problem = document.RootElement;
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal("Service unavailable", problem.GetProperty("title").GetString());
        Assert.Equal("Storage is unavailable. Try again.", problem.GetProperty("detail").GetString());
        Assert.Equal("database_unavailable", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.All(problem.EnumerateObject(), property =>
            Assert.Contains(property.Name, new[] { "type", "title", "status", "detail", "instance", "code", "traceId" }));
        Assert.DoesNotContain(uri, text);
        Assert.DoesNotContain("mongodb://", text);
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain("healthy", text);
    }

    [Theory]
    [InlineData("{}")] [InlineData("{\"username\":\"valid\"}")] [InlineData("{\"password\":\"Valid-password-123\"}")]
    [InlineData("{\"username\":3,\"password\":\"Valid-password-123\"}")]
    [InlineData("{\"username\":\"valid\",\"password\":\"short\"}")]
    [InlineData("{\"username\":\"valid\",\"password\":\"Valid-password-123\",\"role\":\"Admin\"}")]
    [InlineData("{broken")]
    public async Task Invalid_missing_unknown_and_malformed_json_return_safe_400(string body)
    {
        using var client = app.CreateClient(); var response = await client.PostAsync("/api/login", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.Contains("validation_error", text); Assert.Contains("traceId", text); Assert.DoesNotContain("Valid-password-123", text);
    }

    [Theory]
    [InlineData("/api/health?unexpected=1")] [InlineData("/api/login?unexpected=1")]
    public async Task Unknown_query_parameters_are_rejected(string path)
    {
        using var client = app.CreateClient(); var response = path.Contains("login") ? await client.PostAsJsonAsync(path, new { username="valid",password="Test-password-123!" }) : await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("missing")] [InlineData("<script>alert(1)</script>")] [InlineData("{\"$ne\":null}")]
    public async Task Unknown_literal_usernames_return_generic_401_without_secret_or_input_echo(string username)
    {
        using var client = app.CreateClient(); var response = await client.PostAsJsonAsync("/api/login",new { username,password="Test-password-123!" });
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode); var text=await response.Content.ReadAsStringAsync(); Assert.Contains("invalid_credentials",text); Assert.DoesNotContain(username,text); Assert.DoesNotContain("Test-password",text);
    }

    [Theory]
    [InlineData("missing")] [InlineData("invalid")] [InlineData("expired")] [InlineData("tampered")]
    [InlineData("wrong_issuer")] [InlineData("wrong_audience")] [InlineData("role_mismatch")] [InlineData("inactive")] [InlineData("deleted")]
    public async Task Protected_requests_reject_invalid_expired_tampered_and_stale_principals(string scenario)
    {
        var user=await app.SeedAsync(); using var client=app.CreateClient();
        var token=app.Services.GetRequiredService<TokenService>().Issue(user).AccessToken;
        if(scenario is "expired" or "wrong_issuer" or "wrong_audience" or "role_mismatch")
        {
            var now=DateTime.UtcNow; var jwt=new JwtSecurityToken(scenario=="wrong_issuer"?"wrong":"umm-tests",scenario=="wrong_audience"?"wrong":"umm-test-browser",[new Claim("sub",user.Id.ToString()),new Claim("role",scenario=="role_mismatch"?"Admin":"User")],now.AddHours(-1),scenario=="expired"?now.AddMinutes(-1):now.AddMinutes(30),new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(app.Secret)),SecurityAlgorithms.HmacSha256));
            token=new JwtSecurityTokenHandler().WriteToken(jwt);
        }
        if(scenario=="invalid") token="not-a-token";
        if(scenario=="tampered") { var parts=token.Split('.'); parts[1]=Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("User","Admin"))); token=string.Join('.',parts); }
        if(scenario=="inactive") await app.Mongo.Users.UpdateOneAsync(x=>x.Id==user.Id,Builders<UserManagement.Api.Users.UserDocument>.Update.Set(x=>x.IsActive,false));
        if(scenario=="deleted") await app.Mongo.Users.DeleteOneAsync(x=>x.Id==user.Id);
        if(scenario!="missing") client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        var response=await client.GetAsync("/api/auth-probe"); Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode); Assert.Contains("invalid_token",await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false, "Test-password-123!")]
    [InlineData(true, "Wrong-password-123!")]
    public async Task Persisted_inactive_or_wrong_credentials_return_generic_401(bool active, string password)
    {
        var user = await app.SeedAsync("User", active); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new { username = user.Username, password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_credentials", await response.Content.ReadAsStringAsync());
        var log = await app.Mongo.Logs.Find(x => x.UserId == user.Id).SingleAsync();
        Assert.Equal("failure", log.Status);
    }

    [Theory]
    [InlineData(11)] [InlineData(73)]
    public async Task Password_byte_limits_return_400_without_returning_input(int length)
    {
        using var client = app.CreateClient(); var password = new string('z', length);
        var response = await client.PostAsJsonAsync("/api/login", new { username = "valid", password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(password, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task User_role_is_allowed_on_authenticated_route_but_forbidden_on_admin_route()
    {
        var user=await app.SeedAsync(); using var client=app.CreateClient(); client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",app.Services.GetRequiredService<TokenService>().Issue(user).AccessToken);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/auth-probe")).StatusCode);
        var denied=await client.GetAsync("/api/auth-probe/admin"); Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode); Assert.Contains("forbidden",await denied.Content.ReadAsStringAsync());
    }
}
