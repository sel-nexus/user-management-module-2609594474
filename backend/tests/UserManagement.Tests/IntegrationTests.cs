using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Auth;
using UserManagement.Api.Users;
using UserManagement.Api.Dashboard;
using Xunit;
namespace UserManagement.Tests;

// Exercise cross-feature contracts through HTTP and verify their shared durable Mongo state.
public sealed class IntegrationTests : IAsyncLifetime
{
    private readonly TestApp app = new();
    public Task InitializeAsync() => app.InitializeAsync();
    public Task DisposeAsync() => app.DisposeAsync();

    // Authenticate via the real public endpoint, never bypassing token issuance.
    private async Task<HttpClient> Login(string username, string password)
    {
        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonSerializer.Deserialize<LoginResponse>(await response.Content.ReadAsStringAsync(), TestApp.Json)!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        return client;
    }

    // Require explicit successful status before reading an agreed JSON contract.
    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.Equal(status, response.StatusCode);
        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), TestApp.Json)!;
    }

    [Fact]
    public async Task Create_discover_profile_password_audit_dashboard_and_deactivate_share_durable_identity()
    {
        var admin = await app.SeedAsync("Admin");
        using var manager = await Login(admin.Username, "Test-password-123!");
        var created = await Read<UserResponse>(await manager.PostAsJsonAsync("/api/users", new { username = "journey-account", password = "Journey-password-123!", city = "Pune", country = "IN" }), HttpStatusCode.Created);
        using var user = await Login(created.Username, "Journey-password-123!");
        var profile = await Read<UserResponse>(await user.GetAsync("/api/profile"));
        Assert.Equal(created.Id, profile.Id);
        await Read<UserResponse>(await user.PutAsJsonAsync("/api/profile", new { city = "Mumbai", education = "Degree" }));
        var discoveryResponse = await manager.GetAsync("/api/users?search=Mumbai&country=IN&sort=city&direction=desc&limit=1&skip=0");
        Assert.Equal(HttpStatusCode.OK, discoveryResponse.StatusCode);
        var page = JsonDocument.Parse(await discoveryResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, page.GetProperty("total").GetInt64());
        Assert.Equal(created.Id, page.GetProperty("items")[0].GetProperty("id").GetString());
        var detail = await Read<UserResponse>(await manager.GetAsync("/api/users/" + created.Id));
        Assert.Equal("Mumbai", detail.City);
        Assert.Equal("Degree", detail.Education);
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsJsonAsync("/api/profile/password", new { current_password = "Journey-password-123!", new_password = "New-journey-password-123!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsJsonAsync("/api/login", new { username = created.Username, password = "Journey-password-123!" })).StatusCode);
        using var changed = await Login(created.Username, "New-journey-password-123!");
        Assert.Equal(created.Id, (await Read<UserResponse>(await changed.GetAsync("/api/profile"))).Id);
        var dashboard = await Read<DashboardSummary>(await manager.GetAsync("/api/dashboard"));
        Assert.Equal(2, dashboard.Total);
        Assert.Equal(2, dashboard.Active);
        Assert.Contains(dashboard.CountryDistribution, x => x.Country == "IN" && x.Count == 1);
        Assert.True(dashboard.LoginSuccess >= 3);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync("/api/users/" + created.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await changed.GetAsync("/api/profile")).StatusCode);
        var after = await Read<DashboardSummary>(await manager.GetAsync("/api/dashboard"));
        Assert.Equal(2, after.Total); Assert.Equal(1, after.Active); Assert.Equal(1, after.Inactive);
        var saved = await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(created.Id)).SingleAsync();
        Assert.False(saved.IsActive); Assert.Equal("Mumbai", saved.City);
        Assert.True(BCrypt.Net.BCrypt.Verify("New-journey-password-123!", saved.PasswordHash));
        var logsResponse = await manager.GetAsync("/api/logs?limit=100&skip=0");
        Assert.Equal(HttpStatusCode.OK, logsResponse.StatusCode);
        var logsText = await logsResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password_hash", logsText); Assert.DoesNotContain("Journey-password-123!", logsText);
        var events = await app.Mongo.Logs.Find(x => x.UserId == saved.Id).ToListAsync();
        foreach (var action in new[] { "create_user", "update_profile", "change_password", "deactivate_user" })
            Assert.Contains(events, x => x.Action == action && x.Status == "success");
    }

    [Fact]
    public async Task Successful_administrator_edit_flows_to_own_profile_discovery_audit_and_country_summary()
    {
        var admin = await app.SeedAsync("Admin");
        using var manager = await Login(admin.Username, "Test-password-123!");
        var account = await Read<UserResponse>(await manager.PostAsJsonAsync("/api/users", new { username = "admin-edited-user", password = "Test-password-123!", country = "IN" }), HttpStatusCode.Created);
        using var user = await Login(account.Username, "Test-password-123!");
        var edited = await Read<UserResponse>(await manager.PutAsJsonAsync("/api/users/" + account.Id, new { username = "renamed-account", country = "GB", city = "London" }));
        Assert.Equal(account.Id, edited.Id);
        var profile = await Read<UserResponse>(await user.GetAsync("/api/profile"));
        Assert.Equal("renamed-account", profile.Username); Assert.Equal("GB", profile.Country); Assert.Equal("London", profile.City);
        var discovery = await manager.GetAsync("/api/users?search=renamed-account&country=GB&limit=20&skip=0");
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        var page = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, page.GetProperty("total").GetInt64()); Assert.Equal(account.Id, page.GetProperty("items")[0].GetProperty("id").GetString());
        var audit = await manager.GetAsync("/api/logs?limit=100&skip=0"); Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var events = JsonDocument.Parse(await audit.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(events.Where(x => x.GetProperty("action").GetString() == "update_user" && x.GetProperty("status").GetString() == "success" && x.GetProperty("user_id").GetString() == account.Id));
        var dashboard = await Read<DashboardSummary>(await manager.GetAsync("/api/dashboard"));
        Assert.Equal(2, dashboard.Total); Assert.Contains(dashboard.CountryDistribution, x => x.Country == "GB" && x.Count == 1);
        Assert.DoesNotContain(dashboard.CountryDistribution, x => x.Country == "IN");
        var saved = await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(account.Id)).SingleAsync();
        Assert.Equal("renamed-account", saved.Username); Assert.Equal("GB", saved.Country); Assert.Equal("London", saved.City);
    }

    [Fact]
    public async Task Failed_cross_feature_writes_leave_original_account_profile_discovery_and_summary_consistent()
    {
        var admin = await app.SeedAsync("Admin"); using var manager = await Login(admin.Username, "Test-password-123!");
        var a = await Read<UserResponse>(await manager.PostAsJsonAsync("/api/users", new { username = "integrity-a", password = "Test-password-123!", city = "Original" }), HttpStatusCode.Created);
        var b = await Read<UserResponse>(await manager.PostAsJsonAsync("/api/users", new { username = "integrity-b", password = "Test-password-123!" }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync("/api/users/" + a.Id, new { username = b.Username, city = "Must not persist" })).StatusCode);
        using var user = await Login(a.Username, "Test-password-123!");
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsJsonAsync("/api/profile", new { id = b.Id, city = "Stolen" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/users/" + b.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal("Original", (await Read<UserResponse>(await user.GetAsync("/api/profile"))).City);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.DeleteAsync("/api/users/" + ObjectId.GenerateNewId())).StatusCode);
        var summary = await Read<DashboardSummary>(await manager.GetAsync("/api/dashboard"));
        Assert.Equal(3, summary.Total); Assert.Equal(3, summary.Active);
        Assert.Equal("Original", (await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(a.Id)).SingleAsync()).City);
        Assert.Null((await app.Mongo.Users.Find(x => x.Id == ObjectId.Parse(b.Id)).SingleAsync()).City);
        Assert.Equal(1, await app.Mongo.Logs.CountDocumentsAsync(x => x.UserId == ObjectId.Parse(a.Id) && x.Action == "update_user" && x.Status == "failure"));
    }
}
