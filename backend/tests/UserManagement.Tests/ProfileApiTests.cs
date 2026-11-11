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
using UserManagement.Api.Profile;
using UserManagement.Api.Audit;
using UserManagement.Api.Common;
using Xunit;
namespace UserManagement.Tests;

// Exercise every HTTP trust boundary against the actual application and isolated Mongo database.
public sealed class ProfileApiTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();
    private HttpClient Client(UserDocument? user = null)
    {
        var client = app.CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Services.GetRequiredService<TokenService>().Issue(user).AccessToken);
        return client;
    }
    private static Task<HttpResponseMessage> Send(HttpClient client, string operation, string suffix = "", string? body = null)
    {
        var method = operation == "read" ? "GET" : operation == "update" ? "PUT" : "POST";
        var path = "/api/profile" + (operation == "password" ? "/password" : "") + suffix;
        return client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : new StringContent(body ?? (operation == "update" ? "{}" : "{\"current_password\":\"Test-password-123!\",\"new_password\":\"New-password-123!\"}"), Encoding.UTF8, "application/json") });
    }
    private Task<UserDocument> Saved(ObjectId id) => app.Mongo.Users.Find(x => x.Id == id).SingleAsync();
    private static async Task<UserResponse> Safe(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var text = await response.Content.ReadAsStringAsync(); var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(new[] { "address", "city", "country", "created_at", "education", "id", "is_active", "role", "updated_at", "username" }, body.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.DoesNotContain("password", text); Assert.DoesNotContain("aadhaar", text); Assert.DoesNotContain("_id", text);
        return JsonSerializer.Deserialize<UserResponse>(text, TestApp.Json)!;
    }
    private static async Task Problem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); var text = await response.Content.ReadAsStringAsync(); var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal(status, problem.GetProperty("status").GetInt32()); Assert.Equal(code, problem.GetProperty("code").GetString()); Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("Unapproved-secret", text); Assert.DoesNotContain("Test-password-123!", text); Assert.DoesNotContain("mongodb", text);
    }
    [Theory] [InlineData("Admin")] [InlineData("User")]
    public async Task Both_roles_read_and_update_only_own_profile_clear_null_and_change_password(string role)
    {
        var user = await app.SeedAsync(role); var other = await app.SeedAsync(); var otherBefore = await Saved(other.Id); var before = await Saved(user.Id); using var client = Client(user);
        Assert.Equal(UserResponse.From(before), await Safe(await Send(client, "read")));
        var updated = await Safe(await Send(client, "update", body: "{\"address\":\"Example address\",\"city\":\"Example city\",\"country\":\"Example country\",\"education\":\"Example degree\"}"));
        Assert.Equal(user.Id.ToString(), updated.Id); Assert.Equal(user.Username, updated.Username); Assert.Equal("Example city", updated.City); Assert.Equal(role, updated.Role);
        Assert.Equal(UserResponse.From(await Saved(user.Id)), updated); Assert.Equal(user.PasswordHash, (await Saved(user.Id)).PasswordHash);
        await Safe(await Send(client, "update", body: "{\"address\":null,\"city\":null,\"country\":null,\"education\":null}"));
        var cleared = await Saved(user.Id); Assert.Null(cleared.Address); Assert.Null(cleared.City); Assert.Null(cleared.Country); Assert.Null(cleared.Education);
        await Safe(await Send(client, "update")); Assert.Equal(cleared.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var changed = await Send(client, "password"); Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode); Assert.Equal("", await changed.Content.ReadAsStringAsync());
        var saved = await Saved(user.Id); Assert.StartsWith("$2a$12$", saved.PasswordHash); Assert.True(BCrypt.Net.BCrypt.Verify("New-password-123!", saved.PasswordHash)); Assert.False(BCrypt.Net.BCrypt.Verify("Test-password-123!", saved.PasswordHash));
        Assert.Equal(otherBefore.ToBsonDocument(), (await Saved(other.Id)).ToBsonDocument());
        using var anonymous = Client();
        await Problem(await anonymous.PostAsJsonAsync("/api/login", new { username = user.Username, password = "Test-password-123!" }), 401, "invalid_credentials");
        var login = await anonymous.PostAsJsonAsync("/api/login", new { username = user.Username, password = "New-password-123!" }); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(3, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "update_profile" && x.Status == "success"));
        var log = await app.Mongo.Logs.Find(x => x.Action == "change_password").SingleAsync(); Assert.Equal(user.Id, log.UserId); Assert.Equal("success", log.Status);
    }
    public static IEnumerable<object[]> InvalidBodies()
    {
        foreach (var operation in new[] { "update", "password" })
        {
            foreach (var body in new[] { "", "null", "[]", "{broken" }) yield return [operation, body];
            foreach (var field in new[] { "username", "id", "_id", "user_id", "sub", "role", "is_active", "created_at", "updated_at", "password", "password_hash", "aadhaar", "pan", "unknown", "$set", "$where", "Address" })
            {
                var input = operation == "update" ? new Dictionary<string,object?> { ["city"] = "Must not persist" } : new Dictionary<string,object?> { ["current_password"] = "Test-password-123!", ["new_password"] = "New-password-123!" };
                input[field] = "Unapproved-secret"; yield return [operation, JsonSerializer.Serialize(input)];
            }
        }
        foreach (var field in new[] { "address", "city", "country", "education" })
            foreach (var value in new object[] { 42, true, new[] { "x" }, new Dictionary<string,object?> { ["$ne"] = null }, new string('x', field == "address" ? 257 : 101) })
                yield return ["update", JsonSerializer.Serialize(new Dictionary<string,object?> { [field] = value })];
        foreach (var field in new[] { "current_password", "new_password" })
            foreach (var value in new object?[] { null, "", "short", new string('x', 11), new string('x', 73), new string('é', 37), new string('é', 5), 42, true, new[] { "x" }, new Dictionary<string,object?> { ["$ne"] = null } })
            {
                var input = new Dictionary<string,object?> { ["current_password"] = "Test-password-123!", ["new_password"] = "New-password-123!" }; input[field] = value;
                yield return ["password", JsonSerializer.Serialize(input)];
            }
        foreach (var body in new[] { "{}", "{\"current_password\":\"Test-password-123!\"}", "{\"new_password\":\"New-password-123!\"}" }) yield return ["password", body];
    }
    [Theory] [MemberData(nameof(InvalidBodies))]
    public async Task Missing_unknown_massassignment_wrong_types_and_sizes_return_safe_400_and_no_write(string operation, string body)
    {
        var user = await app.SeedAsync(); var before = await Saved(user.Id); using var client = Client(user);
        await Problem(await Send(client, operation, body: body), 400, "validation_error");
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => (x.Action == "update_profile" || x.Action == "change_password") && x.Status == "success"));
    }
    [Fact]
    public async Task Invalid_current_password_has_safe_failure_audit_without_credential_echo()
    {
        var user = await app.SeedAsync(); using var client = Client(user); var before = await Saved(user.Id);
        await Problem(await Send(client, "password", body: "{\"current_password\":\"Wrong-password-123!\",\"new_password\":\"New-password-123!\"}"), 400, "invalid_current_password");
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument()); var log = await app.Mongo.Logs.Find(x => x.Action == "change_password").SingleAsync(); Assert.Equal("failure", log.Status);
        Assert.DoesNotContain("password-123", log.ToBsonDocument().ToJson());
    }
    [Theory] [InlineData("read")] [InlineData("update")] [InlineData("password")]
    public async Task All_endpoints_reject_identity_and_any_query_and_have_no_target_id_route(string operation)
    {
        var user = await app.SeedAsync(); var other = await app.SeedAsync(); using var client = Client(user); var before = await Saved(user.Id); var otherBefore = await Saved(other.Id);
        foreach (var query in new[] { "?id=" + other.Id, "?user_id=" + other.Id, "?sub=" + other.Id, "?role=Admin", "?unknown=", "?city[$ne]=x" })
            await Problem(await Send(client, operation, query), 400, "validation_error");
        var path = "/api/profile/" + other.Id; Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument()); Assert.Equal(otherBefore.ToBsonDocument(), (await Saved(other.Id)).ToBsonDocument());
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }
    [Theory]
    [InlineData("missing")] [InlineData("invalid")] [InlineData("expired")] [InlineData("inactive")] [InlineData("deleted")]
    [InlineData("role_mismatch")] [InlineData("wrong_sub")] [InlineData("missing_sub")] [InlineData("malformed_sub")]
    [InlineData("wrong_issuer")] [InlineData("wrong_audience")] [InlineData("bad_signature")]
    public async Task Every_endpoint_denies_invalid_lifetime_identity_role_activity_and_signature(string scenario)
    {
        var user = await app.SeedAsync(); using var client = Client();
        var claims = new List<Claim> { new("sub", user.Id.ToString()), new("role", "User") };
        if (scenario == "role_mismatch") claims[1] = new("role", "Admin");
        if (scenario == "wrong_sub") claims[0] = new("sub", ObjectId.GenerateNewId().ToString());
        if (scenario == "malformed_sub") claims[0] = new("sub", "bad");
        if (scenario == "missing_sub") claims.RemoveAt(0);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(scenario == "wrong_issuer" ? "wrong" : "umm-tests", scenario == "wrong_audience" ? "wrong" : "umm-test-browser", claims, DateTime.UtcNow.AddHours(-2), scenario == "expired" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(30), new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(scenario == "bad_signature" ? new string('z',64) : app.Secret)), SecurityAlgorithms.HmacSha256)));
        if (scenario == "invalid") token = "invalid-token";
        if (scenario == "inactive") await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.IsActive, false));
        if (scenario == "deleted") await app.Mongo.Users.DeleteOneAsync(x => x.Id == user.Id);
        if (scenario != "missing") client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var operation in new[] { "read", "update", "password" }) await Problem(await Send(client, operation), 401, "invalid_token");
        if (scenario != "deleted") { var saved = await Saved(user.Id); Assert.Null(saved.City); Assert.Equal(user.PasswordHash, saved.PasswordHash); }
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }
    [Fact]
    public async Task Field_and_password_exact_byte_bounds_are_accepted_and_literal_strings_are_not_operators()
    {
        var user = await app.SeedAsync(); using var client = Client(user);
        var result = await Safe(await Send(client, "update", body: JsonSerializer.Serialize(new { address = new string('a',256), city = new string('c',100), country = new string('c',100), education = new string('e',100) })));
        Assert.Equal(256, result.Address!.Length); Assert.Equal(100, result.City!.Length); Assert.Equal(100, result.Country!.Length); Assert.Equal(100, result.Education!.Length);
        foreach (var literal in new[] { "<script>alert(1)</script>", "{\"$ne\":null}", "' OR 1=1 --" })
        { var response = await Safe(await Send(client, "update", body: JsonSerializer.Serialize(new { city = literal }))); Assert.Equal(literal, response.City); Assert.Equal(literal, (await Saved(user.Id)).City); }
        var current = "Test-password-123!";
        foreach (var next in new[] { new string('a',12), new string('b',72), new string('é',6), new string('é',36) })
        { var changed = await Send(client, "password", body: JsonSerializer.Serialize(new { current_password = current, new_password = next })); Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode); Assert.True(BCrypt.Net.BCrypt.Verify(next, (await Saved(user.Id)).PasswordHash)); current = next; }
    }
    [Theory] [InlineData("read")] [InlineData("update")] [InlineData("password")]
    public async Task Real_unavailable_profile_repository_returns_safe_503_without_success(string operation)
    {
        var user = await app.SeedAsync(); var before = await Saved(user.Id);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build();
        using var unavailable = app.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(provider => new ProfileService(new MongoUserRepository(new MongoContext(config)), provider.GetRequiredService<PasswordHasher>(), provider.GetRequiredService<IAuditWriter>()))));
        using var client = unavailable.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Services.GetRequiredService<TokenService>().Issue(user).AccessToken);
        await Problem(await Send(client, operation), 503, "database_unavailable"); Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "success"));
    }
}
