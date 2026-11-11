using MongoDB.Driver;
using UserManagement.Api.Users;
using UserManagement.Api.Auth;
using UserManagement.Api.Audit;
namespace UserManagement.Api.Common;

// Apply explicit non-destructive indexes and optional one-shot bootstrap; never promote/reset users.
public sealed class DatabaseSetup(MongoContext context, IUserRepository users, PasswordHasher passwords, IConfiguration config)
{
    // Prepare persistence before serving; fail startup rather than silently offering unusable auth.
    public async Task InitializeAsync(CancellationToken ct)
    {
        await context.PingAsync(ct);
        await context.Users.Indexes.CreateOneAsync(new CreateIndexModel<UserDocument>(Builders<UserDocument>.IndexKeys.Ascending(x => x.Username), new CreateIndexOptions { Unique = true, Name = "username_unique" }), cancellationToken: ct);
        await context.Logs.Indexes.CreateOneAsync(new CreateIndexModel<LogDocument>(Builders<LogDocument>.IndexKeys.Descending(x => x.Timestamp).Descending(x => x.Id), new CreateIndexOptions { Name = "timestamp_id" }), cancellationToken: ct);
        if (!config.GetValue<bool>("Bootstrap:Enabled")) return;
        var username = (config["Bootstrap:Username"] ?? "").Trim().ToLowerInvariant();
        if (username.Length is < 3 or > 64) throw new InvalidOperationException("Configure Bootstrap__Username with 3–64 characters.");
        if (await users.FindByUsernameAsync(username, ct) is not null) return;
        var password = config["Bootstrap:Password"] ?? "";
        if (password.Contains("replace-with", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Configure a random Bootstrap__Password.");
        try { await users.InsertAsync(new UserDocument { Username = username, PasswordHash = passwords.Hash(password), Role = "Admin" }, ct); }
        catch (ApiException e) when (e.Status == 409) { /* Another instance bootstrapped first; preserve that account. */ }
    }
}
