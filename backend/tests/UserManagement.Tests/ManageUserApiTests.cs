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
using UserManagement.Api.Users;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using UserManagement.Api.Audit;
using Xunit;
namespace UserManagement.Tests;

// Verify strict HTTP binding, safe projections, persisted authorization and atomic Mongo transitions.
public sealed class ManageUserApiTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();
    private async Task<HttpClient> Admin()
    {
        var admin = await app.SeedAsync("Admin"); var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/login", new { username = admin.Username, password = "Test-password-123!" }); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = JsonSerializer.Deserialize<LoginResponse>(await login.Content.ReadAsStringAsync(), TestApp.Json)!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken); return client;
    }
    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path, string body = "{}") => client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "PUT" ? new StringContent(body, Encoding.UTF8, "application/json") : null });
    private Task<UserDocument> Saved(ObjectId id) => app.Mongo.Users.Find(x => x.Id == id).SingleAsync();
    private static async Task<UserResponse> Safe(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var text = await response.Content.ReadAsStringAsync(); var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(new[] { "address", "city", "country", "created_at", "education", "id", "is_active", "role", "updated_at", "username" }, body.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.DoesNotContain("password", text); Assert.DoesNotContain("_id", text); Assert.DoesNotContain("aadhaar", text);
        var user = JsonSerializer.Deserialize<UserResponse>(text, TestApp.Json)!; Assert.Matches("^[0-9a-f]{24}$", user.Id); Assert.Equal(DateTimeKind.Utc, user.UpdatedAt.Kind); return user;
    }
    [Fact]
    public async Task Admin_read_partial_edit_noop_and_repeat_delete_preserve_safe_real_record()
    {
        using var client = await Admin(); var user = await app.SeedAsync(); var original = await Saved(user.Id); var path = "/api/users/" + user.Id;
        Assert.Equal(UserResponse.From(original), await Safe(await client.GetAsync(path)));
        var edited = await Safe(await Send(client, "PUT", path, "{\"username\":\" Edited-Account \",\"address\":\"Example address\",\"city\":\"Example city\",\"country\":\"Example country\",\"education\":\"Example degree\"}"));
        Assert.Equal("edited-account", edited.Username); Assert.Equal(UserResponse.From(await Saved(user.Id)), edited); Assert.Equal(original.PasswordHash, (await Saved(user.Id)).PasswordHash);
        var beforeNoop = await Saved(user.Id); await Safe(await Send(client, "PUT", path)); Assert.Equal(beforeNoop.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var cleared = await Safe(await Send(client, "PUT", path, "{\"address\":null}")); Assert.Null(cleared.Address); Assert.Equal("Example city", cleared.City);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode); var inactive = await Saved(user.Id); Assert.False(inactive.IsActive);
        var repeated = await client.DeleteAsync(path); Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode); Assert.Equal("", await repeated.Content.ReadAsStringAsync()); Assert.Equal(inactive.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        Assert.False((await Safe(await client.GetAsync(path))).IsActive);
        var inactiveEdit = await Safe(await Send(client, "PUT", path, "{\"city\":\"Inactive edit\"}")); Assert.False(inactiveEdit.IsActive); Assert.Equal("Inactive edit", inactiveEdit.City);
        var saved = await Saved(user.Id); Assert.Equal(original.PasswordHash, saved.PasswordHash); Assert.Equal(original.Role, saved.Role); Assert.Equal(original.CreatedAt, saved.CreatedAt);
        Assert.Equal(1, await app.Mongo.Users.CountDocumentsAsync(x => x.Id == user.Id));
        var login = await app.CreateClient().PostAsJsonAsync("/api/login", new { username = edited.Username, password = "Test-password-123!" }); Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
    [Theory]
    [InlineData("GET")] [InlineData("PUT")] [InlineData("DELETE")]
    public async Task Unavailable_real_lifecycle_repository_returns_safe_503_and_failure_audit(string method)
    {
        var user = await app.SeedAsync(); var admin = await app.SeedAsync("Admin"); var original = await Saved(user.Id);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build();
        using var unavailable = app.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(provider =>
            new UserLifecycleService(new MongoUserRepository(new MongoContext(config)), provider.GetRequiredService<PasswordHasher>(), provider.GetRequiredService<IAuditWriter>()))));
        using var client = unavailable.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Services.GetRequiredService<TokenService>().Issue(admin).AccessToken);
        var response = await Send(client, method, "/api/users/" + user.Id, "{\"city\":\"Unavailable\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("database_unavailable", text); Assert.Contains("traceId", text); Assert.DoesNotContain("mongodb", text);
        Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var log = await app.Mongo.Logs.Find(x => x.Status == "failure").SingleAsync(); Assert.Equal(user.Id, log.UserId);
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "success"));
    }
    public static IEnumerable<object[]> InvalidBodies()
    {
        foreach (var body in new[] { "", "null", "[]", "{broken" }) yield return [body];
        foreach (var field in new[] { "username", "address", "city", "country", "education" })
        {
            foreach (var value in new object[] { 42, true, new[] { "x" }, new Dictionary<string, object?> { ["$ne"] = null }, new string('x', field == "username" ? 65 : field == "address" ? 257 : 101) })
                yield return [JsonSerializer.Serialize(new Dictionary<string, object?> { [field] = value })];
        }
        foreach (var value in new string?[] { null, "", "  ", "ab", " ab " }) yield return [JsonSerializer.Serialize(new { username = value })];
        foreach (var field in new[] { "password", "password_hash", "role", "id", "_id", "is_active", "created_at", "updated_at", "aadhaar", "pan", "$set", "$where", "unknown", "Username" })
            yield return [JsonSerializer.Serialize(new Dictionary<string, object?> { ["city"] = "Must not persist", [field] = "Unapproved-secret" })];
    }
    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Missing_body_wrong_types_extra_fields_and_security_injection_return_safe_400_without_write(string body)
    {
        using var client = await Admin(); var user = await app.SeedAsync(); var original = await Saved(user.Id);
        var response = await Send(client, "PUT", "/api/users/" + user.Id, body); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); var problem = JsonDocument.Parse(text).RootElement; Assert.Equal(400, problem.GetProperty("status").GetInt32()); Assert.Equal("validation_error", problem.GetProperty("code").GetString()); Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("Unapproved-secret", text); Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "update_user" && x.Status == "success"));
    }
    [Theory]
    [InlineData("GET")] [InlineData("PUT")] [InlineData("DELETE")]
    public async Task Every_method_rejects_malformed_missing_and_unknown_queries(string method)
    {
        using var client = await Admin(); var user = await app.SeedAsync(); var original = await Saved(user.Id);
        foreach (var (path, status) in new[] { ("/api/users/not-an-id", 400), ("/api/users/" + ObjectId.GenerateNewId(), 404), ("/api/users/" + user.Id + "?role=Admin", 400) })
        { var response = await Send(client, method, path); Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.Contains("traceId", await response.Content.ReadAsStringAsync()); }
        Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
    }
    [Theory]
    [InlineData("<script>alert(1)</script>")] [InlineData("{\"$ne\":null}")] [InlineData("' OR 1=1 --")]
    public async Task Injection_looking_strings_remain_literal_scalar_values(string literal)
    {
        using var client = await Admin(); var user = await app.SeedAsync();
        var response = await Safe(await Send(client, "PUT", "/api/users/" + user.Id, JsonSerializer.Serialize(new { username = literal, address = literal, city = literal, country = literal, education = literal })));
        Assert.Equal(literal.ToLowerInvariant(), response.Username); var saved = await Saved(user.Id); Assert.Equal(literal, saved.Address); Assert.Equal(literal, saved.City); Assert.Equal(literal, saved.Country); Assert.Equal(literal, saved.Education); Assert.Equal("User", saved.Role);
    }
    [Fact]
    public async Task Duplicate_username_returns_409_without_partial_profile_or_timestamp_write()
    {
        using var client = await Admin(); var user = await app.SeedAsync(); var other = await app.SeedAsync(active: false); var original = await Saved(user.Id);
        var response = await Send(client, "PUT", "/api/users/" + user.Id, JsonSerializer.Serialize(new { username = " " + other.Username.ToUpperInvariant() + " ", city = "No write" }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("duplicate_username", await response.Content.ReadAsStringAsync()); Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var log = await app.Mongo.Logs.Find(x => x.Action == "update_user").SingleAsync(); Assert.Equal("failure", log.Status);
    }
    [Theory]
    [InlineData("anonymous", 401)] [InlineData("User", 403)] [InlineData("expired", 401)] [InlineData("inactive", 401)] [InlineData("invalid", 401)]
    public async Task All_methods_enforce_token_lifetime_active_principal_and_Admin_role(string scenario, int expected)
    {
        using var client = app.CreateClient(); var target = await app.SeedAsync(); var original = await Saved(target.Id);
        if (scenario != "anonymous")
        {
            var caller = await app.SeedAsync(scenario == "User" ? "User" : "Admin"); var token = app.Services.GetRequiredService<TokenService>().Issue(caller).AccessToken;
            if (scenario == "expired") token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("umm-tests", "umm-test-browser", [new Claim("sub", caller.Id.ToString()), new Claim("role", "Admin")], DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1), new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(app.Secret)), SecurityAlgorithms.HmacSha256)));
            if (scenario == "invalid") token = "invalid-token";
            if (scenario == "inactive") await app.Mongo.Users.UpdateOneAsync(x => x.Id == caller.Id, Builders<UserDocument>.Update.Set(x => x.IsActive, false));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        foreach (var method in new[] { "GET", "PUT", "DELETE" })
        { var response = await Send(client, method, "/api/users/" + target.Id, "{\"city\":\"Denied\"}"); Assert.Equal((HttpStatusCode)expected, response.StatusCode); Assert.Contains(expected == 403 ? "forbidden" : "invalid_token", await response.Content.ReadAsStringAsync()); }
        Assert.Equal(original.ToBsonDocument(), (await Saved(target.Id)).ToBsonDocument()); Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "read_user" || x.Action == "update_user" || x.Action == "deactivate_user"));
    }
}
