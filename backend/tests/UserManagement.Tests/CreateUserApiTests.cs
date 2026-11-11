using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Audit;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Exercise the real controller, strict JSON binding, persisted authorization and Mongo writes.
public sealed class CreateUserApiTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();

    private async Task<HttpClient> AdminClientAsync()
    {
        var admin = await app.SeedAsync("Admin");
        var client = app.CreateClient();
        // Obtain the actual API-issued credential rather than substituting an authentication handler.
        var login = await client.PostAsJsonAsync("/api/login", new { username = admin.Username, password = "Test-password-123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = JsonSerializer.Deserialize<LoginResponse>(await login.Content.ReadAsStringAsync(), TestApp.Json)!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return client;
    }

    [Fact]
    public async Task Admin_create_returns_201_location_safe_snake_case_and_real_persisted_account()
    {
        using var client = await AdminClientAsync(); var before = UserDocument.CanonicalizeTimestamp(DateTime.UtcNow);
        var response = await client.PostAsJsonAsync("/api/users", new { username = " New-Account ", password = "Test-password-123!", address = "Example address", city = "Example city", country = "Example country", education = "Example education" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); var body = JsonDocument.Parse(text).RootElement;
        var keys = body.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "address", "city", "country", "created_at", "education", "id", "is_active", "role", "updated_at", "username" }, keys);
        Assert.DoesNotContain("password", text); Assert.DoesNotContain("aadhaar", text); Assert.DoesNotContain("pan", text); Assert.DoesNotContain("_id", text);
        var user = JsonSerializer.Deserialize<UserResponse>(text, TestApp.Json)!;
        Assert.Matches("^[0-9a-f]{24}$", user.Id); Assert.Equal("/api/users/" + user.Id, response.Headers.Location!.OriginalString);
        Assert.Equal("new-account", user.Username); Assert.Equal("User", user.Role); Assert.True(user.IsActive);
        Assert.InRange(user.CreatedAt, before, DateTime.UtcNow); Assert.Equal(DateTimeKind.Utc, user.CreatedAt.Kind); Assert.Equal(user.CreatedAt, user.UpdatedAt);
        var saved = await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(user.Id)).SingleAsync();
        Assert.Equal(user.Username, saved.Username); Assert.Equal(user.Address, saved.Address); Assert.Equal(user.City, saved.City); Assert.Equal(user.Country, saved.Country); Assert.Equal(user.Education, saved.Education);
        Assert.Equal("User", saved.Role); Assert.True(saved.IsActive); Assert.StartsWith("$2a$12$", saved.PasswordHash);
        Assert.True(app.Services.GetRequiredService<PasswordHasher>().Verify("Test-password-123!", saved.PasswordHash));
        Assert.DoesNotContain(saved.PasswordHash, text);
        Assert.Equal(saved.CreatedAt, user.CreatedAt); Assert.Equal(saved.UpdatedAt, user.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, user.UpdatedAt.Kind);
        Assert.Equal(0L, user.CreatedAt.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(0L, user.UpdatedAt.Ticks % TimeSpan.TicksPerMillisecond);
        var log = await app.Mongo.Logs.Find(x => x.UserId == saved.Id).SingleAsync(); Assert.Equal("create_user", log.Action); Assert.Equal("success", log.Status);
        using var get = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var retrievedText = await get.Content.ReadAsStringAsync();
        var retrieved = JsonSerializer.Deserialize<UserResponse>(retrievedText, TestApp.Json)!;
        Assert.Equal(user, retrieved);
        var retrievedBody = JsonDocument.Parse(retrievedText).RootElement;
        Assert.Equal(body.GetProperty("created_at").GetString(), retrievedBody.GetProperty("created_at").GetString());
        Assert.Equal(body.GetProperty("updated_at").GetString(), retrievedBody.GetProperty("updated_at").GetString());
    }

    // Missing, null, wrong scalar/container types and both sides of every documented length bound.
    public static IEnumerable<object[]> InvalidBodies()
    {
        var valid = new Dictionary<string, object?> { ["username"] = "valid-account", ["password"] = "Test-password-123!" };
        yield return ["{}"]; yield return ["null"]; yield return ["[]"]; yield return ["{broken"];
        foreach (var field in new[] { "username", "password" })
        {
            var missing = new Dictionary<string, object?>(valid); missing.Remove(field); yield return [JsonSerializer.Serialize(missing)];
            foreach (var value in new object?[] { null, "", "   " })
            { var body = new Dictionary<string, object?>(valid) { [field] = value }; yield return [JsonSerializer.Serialize(body)]; }
        }
        foreach (var field in new[] { "username", "password", "address", "city", "country", "education" })
        {
            foreach (var value in new object[] { 42, true, new[] { "text" }, new Dictionary<string, object?> { ["$ne"] = null } })
            { var body = new Dictionary<string, object?>(valid) { [field] = value }; yield return [JsonSerializer.Serialize(body)]; }
        }
        foreach (var value in new[] { "ab", " ab ", new string('u', 65) })
        { var body = new Dictionary<string, object?>(valid) { ["username"] = value }; yield return [JsonSerializer.Serialize(body)]; }
        foreach (var value in new[] { new string('p', 11), new string('p', 73), new string('é', 37) })
        { var body = new Dictionary<string, object?>(valid) { ["password"] = value }; yield return [JsonSerializer.Serialize(body)]; }
        foreach (var field in new[] { "address", "city", "country", "education" })
        { var body = new Dictionary<string, object?>(valid) { [field] = new string('x', field == "address" ? 257 : 101) }; yield return [JsonSerializer.Serialize(body)]; }
        foreach (var field in new[] { "role", "id", "_id", "is_active", "created_at", "updated_at", "password_hash", "aadhaar", "pan", "$where", "unknown", "Username", "Password" })
        { var body = new Dictionary<string, object?>(valid) { [field] = "unapproved" }; yield return [JsonSerializer.Serialize(body)]; }
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Every_invalid_missing_wrong_type_length_unknown_and_mass_assignment_returns_safe_400_without_write(string body)
    {
        using var client = await AdminClientAsync();
        var count = await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty);
        var response = await client.PostAsync("/api/users", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal(400, problem.GetProperty("status").GetInt32()); Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("Test-password-123!", text); Assert.DoesNotContain("unapproved", text); Assert.DoesNotContain("$2a$", text);
        Assert.Equal(count, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "create_user"));
    }

    [Theory]
    [InlineData(3, 12)] [InlineData(64, 72)]
    public async Task Exact_required_bounds_and_optional_null_empty_or_maximum_values_are_valid(int usernameLength, int passwordLength)
    {
        using var client = await AdminClientAsync();
        var response = await client.PostAsJsonAsync("/api/users", new { username = new string('u', usernameLength), password = new string('p', passwordLength), address = new string('a', 256), city = new string('c', 100), country = new string('x', 100), education = new string('e', 100) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await app.Mongo.Users.Find(x => x.Username == new string('u', usernameLength)).SingleAsync();
        Assert.Equal(256, saved.Address!.Length); Assert.Equal(100, saved.City!.Length); Assert.Equal(100, saved.Country!.Length); Assert.Equal(100, saved.Education!.Length);
        var optional = await client.PostAsJsonAsync("/api/users", new { username = "optional-account", password = new string('é', 6), address = (string?)null, city = "", country = (string?)null, education = "" });
        Assert.Equal(HttpStatusCode.Created, optional.StatusCode);
        var optionalSaved = await app.Mongo.Users.Find(x => x.Username == "optional-account").SingleAsync();
        Assert.Null(optionalSaved.Address); Assert.Equal("", optionalSaved.City); Assert.Null(optionalSaved.Country); Assert.Equal("", optionalSaved.Education);
    }

    [Fact]
    public async Task Missing_optional_fields_default_to_null_and_password_multibyte_maximum_is_accepted()
    {
        using var client = await AdminClientAsync();
        var response = await client.PostAsJsonAsync("/api/users", new { username = "optional-missing", password = new string('é', 36) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await app.Mongo.Users.Find(x => x.Username == "optional-missing").SingleAsync();
        Assert.Null(saved.Address); Assert.Null(saved.City); Assert.Null(saved.Country); Assert.Null(saved.Education);
        Assert.True(new PasswordHasher().Verify(new string('é', 36), saved.PasswordHash));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")] [InlineData("{\"$ne\":null}")] [InlineData(".*(a+)+$")] [InlineData("' OR 1=1 --")]
    public async Task Injection_strings_are_stored_as_literal_profile_and_username_values_not_operators(string literal)
    {
        using var client = await AdminClientAsync();
        var response = await client.PostAsJsonAsync("/api/users", new { username = literal, password = "Test-password-123!", address = literal, city = literal, country = literal, education = literal });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await app.Mongo.Users.Find(x => x.Username == literal.ToLowerInvariant()).SingleAsync();
        Assert.Equal(literal, saved.Address); Assert.Equal(literal, saved.City); Assert.Equal(literal, saved.Country); Assert.Equal(literal, saved.Education);
        Assert.Equal("User", saved.Role); Assert.Equal(2, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Duplicate_returns_409_without_overwriting_original_account()
    {
        using var client = await AdminClientAsync();
        var first = await client.PostAsJsonAsync("/api/users", new { username = "duplicate-account", password = "Original-password-123", city = "Original city" }); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await app.Mongo.Users.Find(x => x.Username == "duplicate-account").SingleAsync();
        var duplicate = await client.PostAsJsonAsync("/api/users", new { username = " DUPLICATE-ACCOUNT ", password = "Different-password-123", city = "Changed city" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode); var problem = await duplicate.Content.ReadAsStringAsync(); Assert.Contains("duplicate_username", problem); Assert.DoesNotContain("Different-password-123", problem);
        var saved = await app.Mongo.Users.Find(x => x.Username == "duplicate-account").SingleAsync();
        Assert.Equal(original.Id, saved.Id); Assert.Equal(original.PasswordHash, saved.PasswordHash); Assert.Equal(original.City, saved.City); Assert.Equal(original.UpdatedAt, saved.UpdatedAt);
        Assert.Equal(1, await app.Mongo.Users.CountDocumentsAsync(x => x.Username == "duplicate-account"));
    }

    [Fact]
    public async Task Concurrent_HTTP_duplicates_return_exactly_one_201_and_one_409()
    {
        using var client = await AdminClientAsync();
        var results = await Task.WhenAll(client.PostAsJsonAsync("/api/users", new { username = "api-race", password = "Test-password-123!" }), client.PostAsJsonAsync("/api/users", new { username = " API-RACE ", password = "Other-password-123!" }));
        var winner = Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        var loser = Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("duplicate_username", await loser.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await winner.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        var saved = await app.Mongo.Users.Find(x => x.Username == "api-race").SingleAsync(); Assert.Equal(id, saved.Id.ToString());
        Assert.Equal(1, await app.Mongo.Users.CountDocumentsAsync(x => x.Username == "api-race"));
    }

    [Theory]
    [InlineData("anonymous", 401)] [InlineData("User", 403)] [InlineData("inactive", 401)] [InlineData("changed-role", 401)] [InlineData("deleted", 401)]
    public async Task Anonymous_User_and_stale_Admin_are_denied_without_create_write(string scenario, int status)
    {
        using var client = app.CreateClient();
        if (scenario != "anonymous")
        {
            var user = await app.SeedAsync(scenario == "User" ? "User" : "Admin");
            var token = app.Services.GetRequiredService<TokenService>().Issue(user).AccessToken;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (scenario == "inactive") await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.IsActive, false));
            if (scenario == "changed-role") await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.Role, "User"));
            if (scenario == "deleted") await app.Mongo.Users.DeleteOneAsync(x => x.Id == user.Id);
        }
        var count = await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty);
        var response = await client.PostAsJsonAsync("/api/users", new { username = "denied-create", password = "Test-password-123!" });
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.Contains(status == 403 ? "forbidden" : "invalid_token", await response.Content.ReadAsStringAsync());
        Assert.Equal(count, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty)); Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "create_user"));
    }

    [Fact]
    public async Task Unavailable_real_create_repository_returns_safe_503_and_no_account()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build();
        using var unavailableApp = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(provider => new UserLifecycleService(new MongoUserRepository(new MongoContext(config)), provider.GetRequiredService<PasswordHasher>(), provider.GetRequiredService<IAuditWriter>()))));
        using var client = unavailableApp.CreateClient();
        // Keep principal revalidation on the live context; only the creation repository is unreachable.
        var admin = await app.SeedAsync("Admin");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Services.GetRequiredService<TokenService>().Issue(admin).AccessToken);
        var response = await client.PostAsJsonAsync("/api/users", new { username = "storage-unavailable", password = "Test-password-123!" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.Contains("database_unavailable", text); Assert.Contains("traceId", text); Assert.DoesNotContain("mongodb", text); Assert.DoesNotContain("Test-password-123!", text);
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(x => x.Username == "storage-unavailable"));
    }

    [Fact]
    public async Task Unknown_query_returns_400_without_create()
    {
        using var client = await AdminClientAsync();
        var response = await client.PostAsJsonAsync("/api/users?role=Admin", new { username = "query-account", password = "Test-password-123!" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("validation_error", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(x => x.Username == "query-account"));
    }
}
