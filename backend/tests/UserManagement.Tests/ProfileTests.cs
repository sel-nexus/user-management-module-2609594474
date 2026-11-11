using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Profile;
using UserManagement.Api.Users;
using UserManagement.Api.Auth;
using UserManagement.Api.Audit;
using UserManagement.Api.Common;
using Xunit;
namespace UserManagement.Tests;

// Test real Mongo branches and requery state, including hashes and race-resistant updates.
public sealed class ProfileTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();
    private ProfileService Service => app.Services.GetRequiredService<ProfileService>();
    private IUserRepository Repo => app.Services.GetRequiredService<IUserRepository>();
    private static ClaimsPrincipal Principal(UserDocument user) => new(new ClaimsIdentity([new Claim("sub", user.Id.ToString()), new Claim("role", user.Role)], "Bearer"));
    private static UpdateProfileRequest Patch(string body) => JsonSerializer.Deserialize<UpdateProfileRequest>(body, TestApp.Json)!;
    private Task<UserDocument> Saved(ObjectId id) => app.Mongo.Users.Find(x => x.Id == id).SingleAsync();

    [Theory]
    [InlineData("Admin")] [InlineData("User")]
    public async Task Own_read_and_partial_updates_preserve_identity_credentials_omissions_and_other_account(string role)
    {
        var user = await app.SeedAsync(role); var other = await app.SeedAsync(); var otherBefore = await Saved(other.Id);
        await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.Address, "Original address").Set(x => x.UpdatedAt, DateTime.UtcNow.AddDays(-1)));
        var before = await Saved(user.Id); var principal = Principal(user);
        principal.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, other.Id.ToString())]));
        Assert.Equal(UserResponse.From(before), await Service.GetAsync(principal, default));
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
        var start = DateTime.UtcNow;
        var result = await Service.UpdateAsync(principal, Patch("""{"city":"Example city","country":"Example country","education":"Example degree"}"""), default);
        var saved = await Saved(user.Id);
        Assert.Equal(UserResponse.From(saved), result); Assert.Equal("Original address", saved.Address);
        Assert.Equal("Example city", saved.City); Assert.Equal("Example country", saved.Country); Assert.Equal("Example degree", saved.Education);
        Assert.Equal(before.Username, saved.Username); Assert.Equal(before.PasswordHash, saved.PasswordHash); Assert.Equal(before.Role, saved.Role); Assert.True(saved.IsActive); Assert.Equal(before.CreatedAt, saved.CreatedAt);
        Assert.InRange(saved.UpdatedAt, start.AddMilliseconds(-1), DateTime.UtcNow); Assert.Equal(DateTimeKind.Utc, saved.UpdatedAt.Kind);
        Assert.Equal(otherBefore.ToBsonDocument(), (await Saved(other.Id)).ToBsonDocument());
        await Service.UpdateAsync(principal, Patch("""{"address":null,"city":null,"country":null,"education":""}"""), default);
        saved = await Saved(user.Id); Assert.Null(saved.Address); Assert.Null(saved.City); Assert.Null(saved.Country); Assert.Equal("", saved.Education);
        var noop = saved.ToBsonDocument(); await Service.UpdateAsync(principal, new(), default); Assert.Equal(noop, (await Saved(user.Id)).ToBsonDocument());
        var logs = await app.Mongo.Logs.Find(x => x.Action == "update_profile").ToListAsync(); Assert.Equal(3, logs.Count);
        Assert.All(logs, log => { Assert.Equal(user.Id, log.UserId); Assert.Equal("success", log.Status); Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.ToBsonDocument().Names.OrderBy(x => x)); });
    }
    public static IEnumerable<object[]> InvalidPatches()
    {
        foreach (var field in new[] { "address", "city", "country", "education" })
            foreach (var value in new object[] { 42, true, new[] { "text" }, new Dictionary<string, object?> { ["$ne"] = null }, new string('x', field == "address" ? 257 : 101) })
                yield return [JsonSerializer.Serialize(new Dictionary<string, object?> { [field] = value })];
    }
    [Theory] [MemberData(nameof(InvalidPatches))]
    public async Task Invalid_field_types_and_sizes_preserve_document_and_audit_failure(string body)
    {
        var user = await app.SeedAsync(); var before = await Saved(user.Id);
        var error = await Assert.ThrowsAsync<ApiException>(() => Service.UpdateAsync(Principal(user), Patch(body), default)); Assert.Equal(400, error.Status);
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var log = await app.Mongo.Logs.Find(x => x.Action == "update_profile").SingleAsync(); Assert.Equal("failure", log.Status);
    }
    [Fact]
    public async Task Exact_field_maximums_literal_injections_and_last_write_wins_are_safe()
    {
        var user = await app.SeedAsync(); var principal = Principal(user);
        await Service.UpdateAsync(principal, Patch(JsonSerializer.Serialize(new { address = new string('a', 256), city = new string('c', 100), country = new string('c', 100), education = new string('e', 100) })), default);
        var saved = await Saved(user.Id); Assert.Equal(256, saved.Address!.Length); Assert.Equal(100, saved.City!.Length); Assert.Equal(100, saved.Country!.Length); Assert.Equal(100, saved.Education!.Length);
        foreach (var literal in new[] { "<script>alert(1)</script>", "{\"$ne\":null}", "' OR 1=1 --" })
        { await Service.UpdateAsync(principal, Patch(JsonSerializer.Serialize(new { city = literal })), default); Assert.Equal(literal, (await Saved(user.Id)).City); }
        Assert.Equal(saved.Country, (await Saved(user.Id)).Country); Assert.Equal(user.PasswordHash, (await Saved(user.Id)).PasswordHash);
    }
    [Theory]
    [InlineData("Admin", "abcdefghijkl")] [InlineData("User", "abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("User", "éééééé")] [InlineData("Admin", "éééééééééééééééééééééééééééééééééééé")]
    public async Task Password_exact_byte_bounds_store_fresh_bcrypt12_and_reject_old_login(string role, string password)
    {
        var user = await app.SeedAsync(role); var other = await app.SeedAsync(); var otherBefore = await Saved(other.Id); var before = await Saved(user.Id);
        await Service.ChangePasswordAsync(Principal(user), new("Test-password-123!", password), default);
        var saved = await Saved(user.Id); Assert.NotEqual(before.PasswordHash, saved.PasswordHash); Assert.StartsWith("$2a$12$", saved.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(password, saved.PasswordHash)); Assert.False(BCrypt.Net.BCrypt.Verify("Test-password-123!", saved.PasswordHash));
        Assert.Equal(before.Username, saved.Username); Assert.Equal(before.Role, saved.Role); Assert.Equal(before.CreatedAt, saved.CreatedAt); Assert.True(saved.IsActive);
        Assert.Equal(otherBefore.ToBsonDocument(), (await Saved(other.Id)).ToBsonDocument());
        var auth = app.Services.GetRequiredService<AuthenticationService>();
        await Assert.ThrowsAsync<ApiException>(() => auth.LoginAsync(new(user.Username, "Test-password-123!"), default));
        Assert.Equal(user.Id.ToString(), (await auth.LoginAsync(new(user.Username, password), default)).User.Id);
        var log = await app.Mongo.Logs.Find(x => x.Action == "change_password").SingleAsync(); Assert.Equal("success", log.Status); Assert.Equal(user.Id, log.UserId);
        Assert.DoesNotContain(password, log.ToBsonDocument().ToJson()); Assert.DoesNotContain(saved.PasswordHash, log.ToBsonDocument().ToJson());
    }
    [Theory]
    [InlineData("Wrong-password-123!", "New-password-123!", "invalid_current_password")]
    [InlineData(null, "New-password-123!", "validation_error")] [InlineData("", "New-password-123!", "validation_error")]
    [InlineData("Test-password-123!", null, "validation_error")] [InlineData("Test-password-123!", "", "validation_error")]
    [InlineData("Test-password-123!", "12345678901", "validation_error")]
    public async Task Invalid_credentials_never_change_hash_or_claim_success(string? current, string? next, string code)
    {
        var user = await app.SeedAsync(); var before = await Saved(user.Id);
        var error = await Assert.ThrowsAsync<ApiException>(() => Service.ChangePasswordAsync(Principal(user), new(current!, next!), default)); Assert.Equal(400, error.Status); Assert.Equal(code, error.Code);
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var log = await app.Mongo.Logs.Find(x => x.Action == "change_password").SingleAsync(); Assert.Equal("failure", log.Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Over_byte_limit_on_either_password_is_rejected(bool current)
    {
        var user = await app.SeedAsync(); var before = await Saved(user.Id);
        foreach (var invalid in new[] { new string('a', 73), new string('é', 37), new string('é', 5) })
            await Assert.ThrowsAsync<ApiException>(() => Service.ChangePasswordAsync(Principal(user), new(current ? invalid : "Test-password-123!", current ? "New-password-123!" : invalid), default));
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument()); Assert.Equal(3, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "change_password" && x.Status == "failure"));
    }
    [Theory]
    [InlineData("missing")] [InlineData("malformed")] [InlineData("duplicate")] [InlineData("unauthenticated")]
    [InlineData("deleted")] [InlineData("inactive")] [InlineData("role_mismatch")]
    public async Task Every_service_operation_fails_closed_on_invalid_identity_or_state(string scenario)
    {
        var user = await app.SeedAsync(); var principal = Principal(user);
        if (scenario == "missing") principal = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Bearer"));
        if (scenario == "malformed") principal = new(new ClaimsIdentity([new Claim("sub", "bad")], "Bearer"));
        if (scenario == "duplicate") principal.AddIdentity(new ClaimsIdentity([new Claim("sub", user.Id.ToString())]));
        if (scenario == "unauthenticated") principal = new(new ClaimsIdentity([new Claim("sub", user.Id.ToString())]));
        if (scenario == "deleted") await app.Mongo.Users.DeleteOneAsync(x => x.Id == user.Id);
        if (scenario == "inactive") await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.IsActive, false));
        if (scenario == "role_mismatch") await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.Role, "Admin"));
        foreach (var operation in new[] { "read", "update", "password" })
        {
            var error = await Assert.ThrowsAsync<ApiException>(async () => { if (operation == "read") await Service.GetAsync(principal, default); else if (operation == "update") await Service.UpdateAsync(principal, Patch("""{"city":"Denied"}"""), default); else await Service.ChangePasswordAsync(principal, new("Test-password-123!", "New-password-123!"), default); }); Assert.Equal(401, error.Status);
        }
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "success"));
        if (scenario != "deleted") Assert.Equal(user.PasswordHash, (await Saved(user.Id)).PasswordHash);
    }
    [Fact]
    public async Task Atomic_repository_rejects_stale_hash_inactive_role_mismatch_and_unapproved_fields()
    {
        var user = await app.SeedAsync(); var hash = new PasswordHasher().Hash("New-password-123!");
        Assert.False(await Repo.ChangePasswordAsync(user.Id, user.Role, "stale-hash", hash, default));
        Assert.False(await Repo.ChangePasswordAsync(user.Id, "Admin", user.PasswordHash, hash, default));
        await Assert.ThrowsAsync<ApiException>(() => Repo.UpdateProfileAsync(user.Id, user.Role, new Dictionary<string,string?> { ["username"] = "No" }, default));
        Assert.Null(await Repo.UpdateProfileAsync(user.Id, "Admin", new Dictionary<string,string?> { ["city"] = "No" }, default));
        Assert.Null(await Repo.UpdateProfileAsync(ObjectId.GenerateNewId(), "User", new Dictionary<string,string?>(), default));
        await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.IsActive, false));
        Assert.False(await Repo.ChangePasswordAsync(user.Id, user.Role, user.PasswordHash, hash, default));
        Assert.Null(await Repo.UpdateProfileAsync(user.Id, user.Role, new Dictionary<string,string?> { ["city"] = "No" }, default));
        Assert.Equal(user.PasswordHash, (await Saved(user.Id)).PasswordHash); Assert.Null((await Saved(user.Id)).City);
    }
    [Fact]
    public async Task Concurrent_verified_password_updates_leave_exactly_one_atomic_winner()
    {
        var user = await app.SeedAsync(); var hasher = new PasswordHasher(); var one = hasher.Hash("First-password-123!"); var two = hasher.Hash("Second-password-123!");
        var outcomes = await Task.WhenAll(Repo.ChangePasswordAsync(user.Id, user.Role, user.PasswordHash, one, default), Repo.ChangePasswordAsync(user.Id, user.Role, user.PasswordHash, two, default));
        Assert.Single(outcomes, x => x); Assert.Equal(outcomes[0] ? one : two, (await Saved(user.Id)).PasswordHash);
    }
    [Fact]
    public async Task Service_password_change_losing_verified_hash_cas_preserves_winner_and_audits_failure_only()
    {
        var user = await app.SeedAsync(); var hasher = new PasswordHasher();
        var winnerHash = hasher.Hash("Winner-password-123!"); BsonDocument? winner = null;
        var realRepo = new MongoUserRepository(app.Mongo);
        var scheduledRepo = new ProfilePasswordRaceRepository(realRepo, async (id, role, expectedHash, newHash, ct) =>
        {
            // This seam is reached only after the service verified the original password and hashed the proposal.
            Assert.Equal(user.Id, id); Assert.Equal(user.Role, role); Assert.Equal(user.PasswordHash, expectedHash);
            Assert.True(hasher.Verify("Loser-password-123!", newHash));
            Assert.True(await realRepo.ChangePasswordAsync(id, role, expectedHash, winnerHash, ct));
            winner = (await Saved(id)).ToBsonDocument();
        });
        var service = new ProfileService(scheduledRepo, hasher, app.Services.GetRequiredService<IAuditWriter>());
        var error = await Assert.ThrowsAsync<ApiException>(() => service.ChangePasswordAsync(Principal(user), new("Test-password-123!", "Loser-password-123!"), default));
        Assert.Equal(400, error.Status); Assert.Equal("password_change_rejected", error.Code);
        Assert.Equal("Password change could not be completed. Check your current password and retry.", error.Message);
        Assert.NotNull(winner); var saved = await Saved(user.Id);
        Assert.Equal(winnerHash, saved.PasswordHash); Assert.Equal(winner, saved.ToBsonDocument());
        Assert.True(hasher.Verify("Winner-password-123!", saved.PasswordHash));
        Assert.False(hasher.Verify("Test-password-123!", saved.PasswordHash)); Assert.False(hasher.Verify("Loser-password-123!", saved.PasswordHash));
        var log = Assert.Single(await app.Mongo.Logs.Find(Builders<LogDocument>.Filter.Empty).ToListAsync());
        Assert.Equal(user.Id, log.UserId); Assert.Equal("change_password", log.Action); Assert.Equal("failure", log.Status);
        Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.ToBsonDocument().Names.OrderBy(x => x));
    }
    [Fact]
    public async Task Damaged_hash_fails_closed_and_same_password_change_still_uses_fresh_salt()
    {
        var user = await app.SeedAsync(); var principal = Principal(user);
        await Service.ChangePasswordAsync(principal, new("Test-password-123!", "Test-password-123!"), default);
        var saved = await Saved(user.Id); Assert.NotEqual(user.PasswordHash, saved.PasswordHash); Assert.True(BCrypt.Net.BCrypt.Verify("Test-password-123!", saved.PasswordHash));
        await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.PasswordHash, "damaged-hash"));
        var before = await Saved(user.Id);
        var error = await Assert.ThrowsAsync<ApiException>(() => Service.ChangePasswordAsync(principal, new("Test-password-123!", "New-password-123!"), default)); Assert.Equal("invalid_current_password", error.Code);
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        Assert.Equal(1, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "change_password" && x.Status == "success"));
        Assert.Equal(1, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "change_password" && x.Status == "failure"));
    }
    [Theory] [InlineData("update")] [InlineData("password")]
    public async Task Real_validator_write_failure_preserves_state_and_emits_failure_only(string operation)
    {
        var user = await app.SeedAsync(); var before = await Saved(user.Id);
        await app.Mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "users" }, { "validator", new BsonDocument("password_hash", user.PasswordHash).Add("city", BsonNull.Value) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Assert.ThrowsAnyAsync<MongoException>(async () => { if (operation == "update") await Service.UpdateAsync(Principal(user), Patch("""{"city":"Rejected"}"""), default); else await Service.ChangePasswordAsync(Principal(user), new("Test-password-123!", "New-password-123!"), default); });
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument()); var log = await app.Mongo.Logs.Find(x => x.Status == "failure").SingleAsync(); Assert.Equal(user.Id, log.UserId);
    }
    [Theory] [InlineData("read")] [InlineData("update")] [InlineData("password")]
    public async Task Cancellation_and_real_unavailable_repository_propagate_without_success(string operation)
    {
        var user = await app.SeedAsync(); var before = await Saved(user.Id); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        async Task Call(ProfileService service, CancellationToken ct) { if (operation == "read") await service.GetAsync(Principal(user), ct); else if (operation == "update") await service.UpdateAsync(Principal(user), Patch("""{"city":"No"}"""), ct); else await service.ChangePasswordAsync(Principal(user), new("Test-password-123!", "New-password-123!"), ct); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(Service, cancelled.Token));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build();
        var unavailable = new ProfileService(new MongoUserRepository(new MongoContext(config)), new PasswordHasher(), app.Services.GetRequiredService<IAuditWriter>());
        await Assert.ThrowsAnyAsync<TimeoutException>(() => Call(unavailable, default));
        Assert.Equal(before.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument()); Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "success"));
    }
}

// Schedule a competing write at the service CAS boundary; every persistence operation still uses real Mongo.
internal sealed class ProfilePasswordRaceRepository(MongoUserRepository inner, Func<ObjectId, string, string, string, CancellationToken, Task> beforePasswordChange) : IUserRepository
{
    public Task<UserDocument?> FindByUsernameAsync(string username, CancellationToken ct) => inner.FindByUsernameAsync(username, ct);
    public Task<UserDocument?> FindByIdAsync(ObjectId id, CancellationToken ct) => inner.FindByIdAsync(id, ct);
    public Task<UserManagement.Api.Dashboard.PopulationSummary> SummarizeAsync(CancellationToken ct) => inner.SummarizeAsync(ct);
    public Task<UserManagement.Api.Discovery.UserListResponse> ListAsync(UserManagement.Api.Discovery.UserListQuery query, CancellationToken ct) => inner.ListAsync(query, ct);
    public Task InsertAsync(UserDocument user, CancellationToken ct) => inner.InsertAsync(user, ct);
    public Task<UserDocument?> UpdateAsync(ObjectId id, IReadOnlyDictionary<string, string?> changes, CancellationToken ct) => inner.UpdateAsync(id, changes, ct);
    public Task<UserDocument?> UpdateProfileAsync(ObjectId id, string role, IReadOnlyDictionary<string, string?> changes, CancellationToken ct) => inner.UpdateProfileAsync(id, role, changes, ct);
    public async Task<bool> ChangePasswordAsync(ObjectId id, string role, string expectedHash, string newHash, CancellationToken ct)
    {
        await beforePasswordChange(id, role, expectedHash, newHash, ct);
        return await inner.ChangePasswordAsync(id, role, expectedHash, newHash, ct);
    }
    public Task<UserDocument?> DeactivateAsync(ObjectId id, CancellationToken ct) => inner.DeactivateAsync(id, ct);
}
