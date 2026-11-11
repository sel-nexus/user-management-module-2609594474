using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Users;
using UserManagement.Api.Audit;
namespace UserManagement.Api.Common;

// Own the single pooled official driver client and collection handles.
public sealed class MongoContext
{
    public IMongoDatabase Database { get; }
    public IMongoCollection<UserDocument> Users => Database.GetCollection<UserDocument>("users");
    public IMongoCollection<LogDocument> Logs => Database.GetCollection<LogDocument>("logs");
    public MongoContext(IConfiguration configuration)
    {
        var uri = configuration["Mongo:Uri"];
        var name = configuration["Mongo:Database"];
        if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Configure Mongo__Uri and Mongo__Database.");
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        Database = new MongoClient(settings).GetDatabase(name);
    }
    // Check real storage readiness rather than returning fabricated health.
    public Task<BsonDocument> PingAsync(CancellationToken ct) => Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
}
