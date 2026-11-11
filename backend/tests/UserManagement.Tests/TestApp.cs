using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using UserManagement.Api.Users;
using Xunit;
namespace UserManagement.Tests;

// Own a unique real Mongo database per test instance; never touch application databases.
public sealed class TestApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string DatabaseName { get; } = "umm_test_" + Guid.NewGuid().ToString("N");
    public string Secret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    public static JsonSerializerOptions Json { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
    public MongoContext Mongo => Services.GetRequiredService<MongoContext>();
    // Configure real persistence and random signing material before application startup.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = Environment.GetEnvironmentVariable("TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27017",
            ["Mongo:Database"] = DatabaseName, ["Jwt:Secret"] = Secret,
            ["Jwt:Issuer"] = "umm-tests", ["Jwt:Audience"] = "umm-test-browser", ["Jwt:ExpiryMinutes"] = "30",
            ["Bootstrap:Enabled"] = "false"
        }));
        builder.ConfigureServices(services => services.AddControllers().AddApplicationPart(typeof(AuthProbeController).Assembly));
    }
    // Force startup and real Mongo readiness, failing visibly when infrastructure is unavailable.
    public async Task InitializeAsync() { _ = CreateClient(); await Mongo.PingAsync(CancellationToken.None); }
    // Drop only the fixture's unguessable isolated database after disposing HTTP hosts.
    public new async Task DisposeAsync()
    {
        var database = Mongo.Database;
        await database.Client.DropDatabaseAsync(DatabaseName);
        await base.DisposeAsync();
    }
    // Seed a real bcrypt account directly for auth tests; no fake repository is registered.
    public async Task<UserDocument> SeedAsync(string role = "User", bool active = true)
    {
        var user = new UserDocument { Username = "test-" + Guid.NewGuid().ToString("N"), PasswordHash = Services.GetRequiredService<PasswordHasher>().Hash("Test-password-123!"), Role = role, IsActive = active };
        await Services.GetRequiredService<IUserRepository>().InsertAsync(user, CancellationToken.None);
        return user;
    }
}
