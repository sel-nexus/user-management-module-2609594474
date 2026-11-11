using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Audit;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Exercise service branches against the official driver and isolated real Mongo collections.
public sealed class CreateUserTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Create_normalizes_persists_bcrypt_defaults_UTC_and_safe_audit()
    {
        var before = DateTime.UtcNow;
        var result = await app.Services.GetRequiredService<UserLifecycleService>().CreateAsync(new CreateUserRequest(" Fresh-Account ", "Test-password-123!", "Example address", "Example city", "Example country", "Example education"), default);
        var saved = await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(result.Id)).SingleAsync();
        Assert.Equal("fresh-account", saved.Username); Assert.Equal(saved.Username, result.Username);
        Assert.Equal("User", saved.Role); Assert.Equal("User", result.Role); Assert.True(saved.IsActive); Assert.True(result.IsActive);
        Assert.StartsWith("$2a$12$", saved.PasswordHash); Assert.NotEqual("Test-password-123!", saved.PasswordHash);
        Assert.True(app.Services.GetRequiredService<PasswordHasher>().Verify("Test-password-123!", saved.PasswordHash));
        Assert.Equal("Example address", saved.Address); Assert.Equal("Example city", saved.City); Assert.Equal("Example country", saved.Country); Assert.Equal("Example education", saved.Education);
        Assert.Equal(saved.Address, result.Address); Assert.Equal(saved.City, result.City); Assert.Equal(saved.Country, result.Country); Assert.Equal(saved.Education, result.Education);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind); Assert.Equal(DateTimeKind.Utc, saved.UpdatedAt.Kind);
        Assert.InRange(result.CreatedAt, before, DateTime.UtcNow); Assert.Equal(result.CreatedAt, result.UpdatedAt);
        Assert.InRange((result.CreatedAt - saved.CreatedAt).TotalMilliseconds, 0, 1);
        var log = await app.Mongo.Logs.Find(x => x.UserId == saved.Id).SingleAsync();
        Assert.Equal("create_user", log.Action); Assert.Equal("success", log.Status);
        var bson = log.ToBsonDocument(); Assert.False(bson.Contains("password")); Assert.False(bson.Contains("password_hash")); Assert.False(bson.Contains("address"));
    }

    [Theory]
    [InlineData(3, 12)] [InlineData(64, 72)]
    public async Task Exact_bounds_and_absent_optional_fields_are_accepted(int usernameLength, int passwordLength)
    {
        var response = await app.Services.GetRequiredService<UserLifecycleService>().CreateAsync(new CreateUserRequest(new string('a', usernameLength), new string('p', passwordLength)), default);
        var saved = await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(response.Id)).SingleAsync();
        Assert.Null(saved.Address); Assert.Null(saved.City); Assert.Null(saved.Country); Assert.Null(saved.Education);
        Assert.True(new PasswordHasher().Verify(new string('p', passwordLength), saved.PasswordHash));
    }

    [Fact]
    public async Task Maximum_optional_lengths_and_multibyte_password_are_persisted()
    {
        var result = await app.Services.GetRequiredService<UserLifecycleService>().CreateAsync(new CreateUserRequest("valid-account", new string('é', 36), new string('a', 256), new string('b', 100), new string('c', 100), new string('d', 100)), default);
        var saved = await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(result.Id)).SingleAsync();
        Assert.Equal(256, saved.Address!.Length); Assert.Equal(100, saved.City!.Length); Assert.Equal(100, saved.Country!.Length); Assert.Equal(100, saved.Education!.Length);
        Assert.True(new PasswordHasher().Verify(new string('é', 36), saved.PasswordHash));
    }

    public static IEnumerable<object[]> InvalidInputs()
    {
        foreach (var username in new string?[] { null, "", "   ", "ab", " ab ", new string('a', 65) })
            yield return [new CreateUserRequest(username!, "Test-password-123!")];
        foreach (var password in new string?[] { null, "", new string('p', 11), new string('p', 73), new string('é', 37) })
            yield return [new CreateUserRequest("valid-account", password!)];
        yield return [new CreateUserRequest("valid-account", "Test-password-123!", new string('a', 257))];
        yield return [new CreateUserRequest("valid-account", "Test-password-123!", City: new string('a', 101))];
        yield return [new CreateUserRequest("valid-account", "Test-password-123!", Country: new string('a', 101))];
        yield return [new CreateUserRequest("valid-account", "Test-password-123!", Education: new string('a', 101))];
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task Every_invalid_service_field_is_rejected_without_any_write(CreateUserRequest input)
    {
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Services.GetRequiredService<UserLifecycleService>().CreateAsync(input, default));
        Assert.Equal(400, error.Status); Assert.Equal("validation_error", error.Code);
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Sequential_duplicate_preserves_original_credentials_profile_and_activity()
    {
        var service = app.Services.GetRequiredService<UserLifecycleService>();
        var first = await service.CreateAsync(new CreateUserRequest("original", "Original-password-123", City: "Original city"), default);
        var original = await app.Mongo.Users.Find(x => x.Username == "original").SingleAsync();
        await app.Mongo.Users.UpdateOneAsync(x => x.Id == original.Id, Builders<UserDocument>.Update.Set(x => x.IsActive, false));
        var error = await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(new CreateUserRequest(" ORIGINAL ", "Different-password-123", City: "Different city"), default));
        Assert.Equal(409, error.Status); Assert.Equal("duplicate_username", error.Code);
        var saved = await app.Mongo.Users.Find(x => x.Username == "original").SingleAsync();
        Assert.Equal(first.Id, saved.Id.ToString()); Assert.Equal(original.PasswordHash, saved.PasswordHash); Assert.Equal("Original city", saved.City); Assert.False(saved.IsActive);
        Assert.Equal(original.CreatedAt, saved.CreatedAt); Assert.Equal(original.UpdatedAt, saved.UpdatedAt);
        Assert.Equal(1, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        var failure = await app.Mongo.Logs.Find(x => x.Status == "failure").SingleAsync(); Assert.Null(failure.UserId); Assert.Equal("create_user", failure.Action);
    }

    [Fact]
    public async Task Concurrent_creates_use_database_unique_index_and_leave_one_complete_winner()
    {
        var service = app.Services.GetRequiredService<UserLifecycleService>();
        // Start both attempts together; no pre-query or repository mock can arbitrate this race.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(UserResponse? User, ApiException? Error)> Attempt(string username, string city)
        {
            await start.Task;
            try { return (await service.CreateAsync(new CreateUserRequest(username, "Test-password-123!", City: city), default), null); }
            catch (ApiException error) { return (null, error); }
        }
        var one = Task.Run(() => Attempt("Race-account", "First city"));
        var two = Task.Run(() => Attempt(" RACE-ACCOUNT ", "Second city"));
        start.SetResult(); var results = await Task.WhenAll(one, two);
        var winner = Assert.Single(results, x => x.User is not null).User!;
        var loser = Assert.Single(results, x => x.Error is not null).Error!;
        Assert.Equal(409, loser.Status); Assert.Equal("duplicate_username", loser.Code);
        var saved = await app.Mongo.Users.Find(x => x.Username == "race-account").SingleAsync();
        Assert.Equal(winner.Id, saved.Id.ToString()); Assert.Equal(winner.City, saved.City); Assert.True(saved.IsActive); Assert.Equal("User", saved.Role);
        Assert.True(new PasswordHasher().Verify("Test-password-123!", saved.PasswordHash));
        Assert.Equal(1, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        Assert.Equal(1, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "success")); Assert.Equal(1, await app.Mongo.Logs.CountDocumentsAsync(x => x.Status == "failure"));
    }

    [Fact]
    public async Task Unavailable_real_repository_propagates_failure_and_records_only_failure_audit()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build();
        var service = new UserLifecycleService(new MongoUserRepository(new MongoContext(config)), new PasswordHasher(), app.Services.GetRequiredService<IAuditWriter>());
        await Assert.ThrowsAnyAsync<TimeoutException>(() => service.CreateAsync(new CreateUserRequest("unavailable", "Test-password-123!"), default));
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        var log = await app.Mongo.Logs.Find(Builders<LogDocument>.Filter.Empty).SingleAsync(); Assert.Equal("failure", log.Status); Assert.Null(log.UserId);
    }

    [Fact]
    public async Task Mongo_write_validation_failure_propagates_without_success_or_partial_user()
    {
        // Change only this test's isolated collection; provoke a real non-duplicate MongoWriteException.
        await app.Mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", "users" },
            { "validator", new BsonDocument("username", new BsonDocument("$ne", "rejected-account")) },
            { "validationLevel", "strict" }, { "validationAction", "error" }
        });
        await Assert.ThrowsAsync<MongoWriteException>(() => app.Services.GetRequiredService<UserLifecycleService>().CreateAsync(new CreateUserRequest("rejected-account", "Test-password-123!"), default));
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        var log = await app.Mongo.Logs.Find(Builders<LogDocument>.Filter.Empty).SingleAsync();
        Assert.Equal("create_user", log.Action); Assert.Equal("failure", log.Status); Assert.Null(log.UserId);
    }

    [Fact]
    public async Task Audit_delivery_failure_does_not_reverse_persisted_creation()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build();
        var writer = new AuditWriter(new MongoLogRepository(new MongoContext(config)), NullLogger<AuditWriter>.Instance);
        var service = new UserLifecycleService(app.Services.GetRequiredService<IUserRepository>(), new PasswordHasher(), writer);
        var result = await service.CreateAsync(new CreateUserRequest("audit-unavailable", "Test-password-123!"), default);
        var saved = await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(result.Id)).SingleAsync(); Assert.Equal(result.Username, saved.Username);
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Cancelled_create_does_not_persist_or_claim_success()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Services.GetRequiredService<UserLifecycleService>().CreateAsync(new CreateUserRequest("cancelled", "Test-password-123!"), cancelled.Token));
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }
}
