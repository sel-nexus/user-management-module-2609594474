using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Users;
using UserManagement.Api.Common;
using UserManagement.Api.Auth;
using UserManagement.Api.Audit;
using UserManagement.Api.Discovery;
using Xunit;
namespace UserManagement.Tests;

// Exercise lifecycle decisions against isolated real Mongo collections, not repository substitutes.
public sealed class ManageUserTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();
    private UserLifecycleService Service => app.Services.GetRequiredService<UserLifecycleService>();
    private static UpdateUserRequest Patch(string json) => JsonSerializer.Deserialize<UpdateUserRequest>(json, TestApp.Json)!;
    private Task<UserDocument> Saved(ObjectId id) => app.Mongo.Users.Find(x => x.Id == id).SingleAsync();

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Read_projects_selected_active_or_inactive_record_and_audits_without_mutation(bool active)
    {
        var user = await app.SeedAsync(active: active); var original = await Saved(user.Id);
        var result = await Service.GetAsync(user.Id.ToString(), default);
        Assert.Equal(UserResponse.From(original), result);
        Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var log = await app.Mongo.Logs.Find(x => x.Action == "read_user").SingleAsync();
        Assert.Equal(user.Id, log.UserId); Assert.Equal("success", log.Status);
        Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.ToBsonDocument().Names.OrderBy(x => x));
    }

    [Fact]
    public async Task Partial_updates_normalize_preserve_credentials_role_activity_and_omitted_fields_then_clear_null()
    {
        var user = await app.SeedAsync("Admin", false);
        await app.Mongo.Users.UpdateOneAsync(x => x.Id == user.Id, Builders<UserDocument>.Update.Set(x => x.City, "Old city").Set(x => x.Address, "Old address").Set(x => x.UpdatedAt, DateTime.UtcNow.AddDays(-1)));
        var original = await Saved(user.Id); var before = DateTime.UtcNow;
        var result = await Service.UpdateAsync(user.Id.ToString(), Patch("{\"username\":\" Changed-Account \",\"country\":\"Example country\",\"education\":\"Example degree\"}"), default);
        var saved = await Saved(user.Id);
        Assert.Equal("changed-account", saved.Username); Assert.Equal("Old city", saved.City); Assert.Equal("Old address", saved.Address);
        Assert.Equal("Example country", saved.Country); Assert.Equal("Example degree", saved.Education);
        Assert.Equal(original.PasswordHash, saved.PasswordHash); Assert.Equal(original.CreatedAt, saved.CreatedAt); Assert.Equal("Admin", saved.Role); Assert.False(saved.IsActive);
        Assert.InRange(saved.UpdatedAt, before.AddMilliseconds(-1), DateTime.UtcNow); Assert.Equal(DateTimeKind.Utc, saved.UpdatedAt.Kind); Assert.Equal(UserResponse.From(saved), result);
        await Service.UpdateAsync(user.Id.ToString(), Patch("{\"address\":null,\"city\":\"\",\"country\":null,\"education\":null}"), default);
        saved = await Saved(user.Id); Assert.Null(saved.Address); Assert.Equal("", saved.City); Assert.Null(saved.Country); Assert.Null(saved.Education);
        Assert.Equal("changed-account", saved.Username); Assert.Equal(original.PasswordHash, saved.PasswordHash);
        Assert.Equal(2, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "update_user" && x.Status == "success"));
    }

    [Fact]
    public async Task Empty_patch_is_successful_no_op_and_last_write_wins_preserves_other_fields()
    {
        var user = await app.SeedAsync(); var original = await Saved(user.Id);
        await Service.UpdateAsync(user.Id.ToString(), new UpdateUserRequest(), default);
        Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        await Service.UpdateAsync(user.Id.ToString(), Patch("{\"city\":\"First\",\"country\":\"Preserved\"}"), default);
        await Service.UpdateAsync(user.Id.ToString(), Patch("{\"city\":\"Last\"}"), default);
        var saved = await Saved(user.Id); Assert.Equal("Last", saved.City); Assert.Equal("Preserved", saved.Country);
        Assert.Equal(3, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "update_user" && x.Status == "success"));
    }

    [Fact]
    public async Task Duplicate_normalized_username_rolls_back_every_field_and_records_failure()
    {
        var user = await app.SeedAsync(); var other = await app.SeedAsync(active: false); var original = await Saved(user.Id);
        var error = await Assert.ThrowsAsync<ApiException>(() => Service.UpdateAsync(user.Id.ToString(), Patch(JsonSerializer.Serialize(new { username = " " + other.Username.ToUpperInvariant() + " ", city = "Must not persist" })), default));
        Assert.Equal(409, error.Status); Assert.Equal("duplicate_username", error.Code);
        Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        Assert.Equal(2, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        var log = await app.Mongo.Logs.Find(x => x.Action == "update_user").SingleAsync(); Assert.Equal("failure", log.Status); Assert.Equal(user.Id, log.UserId);
    }

    [Fact]
    public async Task Concurrent_duplicate_edits_leave_exactly_one_complete_winner()
    {
        var one = await app.SeedAsync(); var two = await app.SeedAsync();
        async Task<bool> Attempt(UserDocument user, string city)
        {
            try { await Service.UpdateAsync(user.Id.ToString(), Patch(JsonSerializer.Serialize(new { username = "Shared-name", city })), default); return true; }
            catch (ApiException e) { Assert.Equal(409, e.Status); return false; }
        }
        var outcomes = await Task.WhenAll(Attempt(one, "One"), Attempt(two, "Two")); Assert.Single(outcomes, x => x);
        var winner = await app.Mongo.Users.Find(x => x.Username == "shared-name").SingleAsync();
        Assert.Equal(winner.Id == one.Id ? "One" : "Two", winner.City);
        var loser = await Saved(winner.Id == one.Id ? two.Id : one.Id); Assert.Null(loser.City);
        Assert.Equal(1, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "update_user" && x.Status == "failure"));
    }

    public static IEnumerable<object[]> InvalidPatches()
    {
        foreach (var value in new object?[] { null, "", "  ", "ab", " ab ", new string('u', 65), true, 42, new[] { "x" }, new { ne = "x" } })
            yield return [JsonSerializer.Serialize(new { username = value })];
        foreach (var field in new[] { "address", "city", "country", "education" })
            foreach (var value in new object[] { true, 42, new[] { "x" }, new Dictionary<string, object?> { ["$ne"] = null }, new string('x', field == "address" ? 257 : 101) })
                yield return [JsonSerializer.Serialize(new Dictionary<string, object?> { [field] = value })];
    }
    [Theory]
    [MemberData(nameof(InvalidPatches))]
    public async Task Invalid_scalar_or_length_never_writes_and_records_only_failure(string body)
    {
        var user = await app.SeedAsync(); var original = await Saved(user.Id);
        var error = await Assert.ThrowsAsync<ApiException>(() => Service.UpdateAsync(user.Id.ToString(), Patch(body), default)); Assert.Equal(400, error.Status);
        Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var log = await app.Mongo.Logs.Find(x => x.Action == "update_user").SingleAsync(); Assert.Equal("failure", log.Status);
    }
    [Theory]
    [InlineData(3)] [InlineData(64)]
    public async Task Exact_field_bounds_and_own_normalized_username_are_accepted(int length)
    {
        var user = await app.SeedAsync();
        await Service.UpdateAsync(user.Id.ToString(), Patch(JsonSerializer.Serialize(new { username = new string('a', length), address = new string('a', 256), city = new string('c', 100), country = new string('c', 100), education = new string('e', 100) })), default);
        await Service.UpdateAsync(user.Id.ToString(), Patch(JsonSerializer.Serialize(new { username = new string('A', length) })), default);
        var saved = await Saved(user.Id); Assert.Equal(new string('a', length), saved.Username); Assert.Equal(256, saved.Address!.Length); Assert.Equal(100, saved.City!.Length); Assert.Equal(100, saved.Country!.Length); Assert.Equal(100, saved.Education!.Length);
    }
    [Theory]
    [InlineData("read")] [InlineData("update")] [InlineData("delete")]
    public async Task Malformed_and_missing_ids_have_distinct_errors_and_no_success_audit(string action)
    {
        async Task Call(string id) { if (action == "read") await Service.GetAsync(id, default); else if (action == "update") await Service.UpdateAsync(id, new(), default); else await Service.DeactivateAsync(id, default); }
        var malformed = await Assert.ThrowsAsync<ApiException>(() => Call("not-an-id")); Assert.Equal(400, malformed.Status);
        var missing = await Assert.ThrowsAsync<ApiException>(() => Call(ObjectId.GenerateNewId().ToString())); Assert.Equal(404, missing.Status);
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        Assert.Equal(2, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "failure")); Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "success"));
    }
    [Fact]
    public async Task Deactivation_preserves_document_repeat_timestamp_and_query_state_and_blocks_login()
    {
        var user = await app.SeedAsync(); var original = await Saved(user.Id);
        await Service.DeactivateAsync(user.Id.ToString(), default); var inactive = await Saved(user.Id);
        Assert.False(inactive.IsActive); Assert.Equal(original.PasswordHash, inactive.PasswordHash); Assert.Equal(original.Username, inactive.Username); Assert.Equal(original.CreatedAt, inactive.CreatedAt);
        await Service.DeactivateAsync(user.Id.ToString(), default); Assert.Equal(inactive.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var repo = app.Services.GetRequiredService<IUserRepository>();
        Assert.Empty((await repo.ListAsync(new UserListQuery { IsActive = true }, default)).Items);
        Assert.Equal(user.Id.ToString(), Assert.Single((await repo.ListAsync(new UserListQuery { IsActive = false }, default)).Items).Id);
        await Assert.ThrowsAsync<ApiException>(() => app.Services.GetRequiredService<AuthenticationService>().LoginAsync(new LoginRequest(user.Username, "Test-password-123!"), default));
        Assert.Equal(2, await app.Mongo.Logs.CountDocumentsAsync(x => x.Action == "deactivate_user" && x.Status == "success"));
    }
    [Theory]
    [InlineData("read")] [InlineData("update")] [InlineData("delete")]
    public async Task Cancelled_operations_preserve_state_and_never_claim_success(string action)
    {
        var user = await app.SeedAsync(); var original = await Saved(user.Id);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (action == "read") await Service.GetAsync(user.Id.ToString(), cancelled.Token);
            else if (action == "update") await Service.UpdateAsync(user.Id.ToString(), Patch("{\"city\":\"Cancelled\"}"), cancelled.Token);
            else await Service.DeactivateAsync(user.Id.ToString(), cancelled.Token);
        });
        Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "success"));
        var log = await app.Mongo.Logs.Find(x => x.Status == "failure").SingleAsync(); Assert.Equal(user.Id, log.UserId);
    }
    [Theory]
    [InlineData("update")] [InlineData("delete")]
    public async Task Real_Mongo_validator_rejection_preserves_document_and_audits_failure(string action)
    {
        var user = await app.SeedAsync(); var original = await Saved(user.Id);
        await app.Mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "users" }, { "validator", new BsonDocument("is_active", true).Add("city", new BsonDocument("$ne", "Rejected")) },
            { "validationLevel", "strict" }, { "validationAction", "error" }
        });
        await Assert.ThrowsAnyAsync<MongoException>(async () =>
        {
            if (action == "update") await Service.UpdateAsync(user.Id.ToString(), Patch("{\"city\":\"Rejected\"}"), default);
            else await Service.DeactivateAsync(user.Id.ToString(), default);
        });
        Assert.Equal(original.ToBsonDocument(), (await Saved(user.Id)).ToBsonDocument());
        var log = await app.Mongo.Logs.Find(x => x.Status == "failure").SingleAsync(); Assert.Equal(user.Id, log.UserId);
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "success"));
    }
    [Theory]
    [InlineData("read")] [InlineData("update")] [InlineData("delete")]
    public async Task Real_unavailable_repository_propagates_and_records_failure_only(string action)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build();
        var service = new UserLifecycleService(new MongoUserRepository(new MongoContext(config)), new PasswordHasher(), app.Services.GetRequiredService<IAuditWriter>());
        var id = ObjectId.GenerateNewId().ToString();
        await Assert.ThrowsAnyAsync<TimeoutException>(async () => { if (action == "read") await service.GetAsync(id, default); else if (action == "update") await service.UpdateAsync(id, Patch("{\"city\":\"Example\"}"), default); else await service.DeactivateAsync(id, default); });
        var log = await app.Mongo.Logs.Find(Builders<LogDocument>.Filter.Empty).SingleAsync(); Assert.Equal("failure", log.Status);
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }
}
