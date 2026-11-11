using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using UserManagement.Api.Auth;
using UserManagement.Api.Audit;
using UserManagement.Api.Common;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Exercise authentication decisions against isolated real Mongo collections.
public sealed class AuthenticationTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Login_normalizes_username_returns_signed_token_and_persists_safe_success_audit()
    {
        var user = await app.SeedAsync("Admin");
        var result = await app.Services.GetRequiredService<AuthenticationService>().LoginAsync(new LoginRequest(" " + user.Username.ToUpperInvariant() + " ", "Test-password-123!"), default);
        var claims = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(result.AccessToken, app.Services.GetRequiredService<TokenService>().ValidationParameters(), out var token);
        Assert.Equal(user.Id.ToString(), claims.FindFirst("sub")?.Value);
        Assert.Equal("Admin", result.User.Role);
        Assert.InRange(token.ValidTo - token.ValidFrom, TimeSpan.FromMinutes(29.99), TimeSpan.FromMinutes(30.01));
        var log = await app.Mongo.Logs.Find(x => x.UserId == user.Id).SingleAsync();
        Assert.Equal("login", log.Action); Assert.Equal("success", log.Status);
        Assert.Equal(user.PasswordHash, (await app.Mongo.Users.Find(x => x.Id == user.Id).SingleAsync()).PasswordHash);
    }

    [Theory]
    [InlineData("wrong", true, "User")]
    [InlineData("correct", false, "User")]
    [InlineData("correct", true, "Unknown")]
    public async Task Login_rejects_bad_password_inactive_and_unknown_role_with_failure_audit(string password, bool active, string role)
    {
        var user = await app.SeedAsync(role, active);
        var exception = await Assert.ThrowsAsync<ApiException>(() => app.Services.GetRequiredService<AuthenticationService>().LoginAsync(new LoginRequest(user.Username, password == "correct" ? "Test-password-123!" : "Incorrect-password!"), default));
        Assert.Equal(401, exception.Status);
        Assert.Equal("failure", (await app.Mongo.Logs.Find(x => x.UserId == user.Id).SingleAsync()).Status);
    }

    [Fact]
    public async Task Missing_account_audit_omits_identity_and_does_not_create_account()
    {
        await Assert.ThrowsAsync<ApiException>(() => app.Services.GetRequiredService<AuthenticationService>().LoginAsync(new LoginRequest("missing-account", "Test-password-123!"), default));
        Assert.Null((await app.Mongo.Logs.Find(Builders<LogDocument>.Filter.Empty).SingleAsync()).UserId);
        Assert.Equal(0, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }

    [Theory]
    [InlineData(11, false)] [InlineData(12, true)] [InlineData(72, true)] [InlineData(73, false)]
    public void Password_policy_enforces_exact_bcrypt_byte_boundaries(int bytes, bool valid)
    {
        var hasher = new PasswordHasher(); var password = new string('a', bytes);
        if (!valid) { Assert.Throws<ApiException>(() => hasher.Hash(password)); Assert.False(hasher.Verify(password, "invalid")); return; }
        var hash = hasher.Hash(password);
        Assert.StartsWith("$2a$12$", hash); Assert.True(hasher.Verify(password, hash)); Assert.False(hasher.Verify(new string('b', bytes), hash));
    }

    [Fact]
    public void Password_policy_counts_multibyte_input_and_rejects_null_or_corrupt_hash()
    {
        var hasher = new PasswordHasher();
        Assert.Throws<ApiException>(() => hasher.Hash(new string('é', 37)));
        Assert.True(hasher.Verify(new string('é', 6), hasher.Hash(new string('é', 6))));
        Assert.Throws<ApiException>(() => hasher.Validate(null));
        Assert.False(hasher.Verify(null, "broken")); Assert.False(hasher.Verify("Valid-password-123", "broken"));
    }

    [Theory]
    [InlineData("", "Test-password-123!")] [InlineData("ab", "Test-password-123!")] [InlineData("valid", "short")]
    public async Task Invalid_service_input_returns_validation_without_audit(string username, string password)
    {
        var exception = await Assert.ThrowsAsync<ApiException>(() => app.Services.GetRequiredService<AuthenticationService>().LoginAsync(new LoginRequest(username, password), default));
        Assert.Equal(400, exception.Status); Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Bootstrap_is_idempotent_and_never_promotes_or_resets_existing_account()
    {
        var user = await app.SeedAsync();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Bootstrap:Enabled"]="true", ["Bootstrap:Username"]=user.Username, ["Bootstrap:Password"]="Different-password-123" }).Build();
        var setup = new DatabaseSetup(app.Mongo, app.Services.GetRequiredService<IUserRepository>(), new PasswordHasher(), config);
        await setup.InitializeAsync(default); await setup.InitializeAsync(default);
        var saved = await app.Mongo.Users.Find(x => x.Id == user.Id).SingleAsync();
        Assert.Equal("User", saved.Role); Assert.Equal(user.PasswordHash, saved.PasswordHash);
        Assert.Equal(1, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Audit_storage_failure_does_not_reverse_successful_credentials()
    {
        var user = await app.SeedAsync();
        var unavailableConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:1", ["Mongo:Database"] = app.DatabaseName }).Build();
        var unavailable = new MongoContext(unavailableConfig);
        var writer = new AuditWriter(new MongoLogRepository(unavailable), Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditWriter>.Instance);
        var service = new AuthenticationService(app.Services.GetRequiredService<IUserRepository>(), new PasswordHasher(), app.Services.GetRequiredService<TokenService>(), writer);
        var response = await service.LoginAsync(new LoginRequest(user.Username, "Test-password-123!"), default);
        Assert.Equal(user.Id.ToString(), response.User.Id);
        Assert.Equal(0, await app.Mongo.Logs.CountDocumentsAsync(Builders<LogDocument>.Filter.Empty));
    }

    [Fact]
    public async Task Bootstrap_creates_only_one_real_admin_with_bcrypt_hash()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Bootstrap:Enabled"] = "true", ["Bootstrap:Username"] = " Fresh-admin ", ["Bootstrap:Password"] = "Bootstrap-password-123" }).Build();
        var setup = new DatabaseSetup(app.Mongo, app.Services.GetRequiredService<IUserRepository>(), new PasswordHasher(), config);
        await setup.InitializeAsync(default); await setup.InitializeAsync(default);
        var saved = await app.Mongo.Users.Find(x => x.Username == "fresh-admin").SingleAsync();
        Assert.Equal("Admin", saved.Role); Assert.True(saved.IsActive);
        Assert.True(new PasswordHasher().Verify("Bootstrap-password-123", saved.PasswordHash));
        Assert.Equal(1, await app.Mongo.Users.CountDocumentsAsync(Builders<UserDocument>.Filter.Empty));
    }

    [Fact]
    public void Known_default_signing_material_is_rejected()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Jwt:Secret"]=new string('x',64)+"change-me", ["Jwt:Issuer"]="issuer", ["Jwt:Audience"]="audience", ["Jwt:ExpiryMinutes"]="30" }).Build();
        Assert.Throws<InvalidOperationException>(() => new TokenService(config));
    }
}
