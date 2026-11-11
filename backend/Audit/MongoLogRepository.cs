using MongoDB.Driver;
using MongoDB.Bson;
using UserManagement.Api.Dashboard;
using UserManagement.Api.Common;
namespace UserManagement.Api.Audit;

// Append and retrieve safe events through real Mongo storage, with bounded reads.
public sealed class MongoLogRepository(MongoContext context)
{
    // Count all recorded activity and exact login outcomes within inclusive UTC bounds in Mongo.
    public async Task<ActivitySummary> SummarizeAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        // BSON dates have millisecond precision: ceil the lower bound so an older fraction is not included.
        start = start.ToUniversalTime(); end = end.ToUniversalTime();
        var remainder = start.Ticks % TimeSpan.TicksPerMillisecond;
        if (remainder != 0) start = start.AddTicks(TimeSpan.TicksPerMillisecond - remainder);
        var pipeline = new[] {
            new BsonDocument("$match", new BsonDocument("timestamp", new BsonDocument { { "$type", "date" }, { "$gte", start }, { "$lte", end } })),
            BsonDocument.Parse("""
            { "$group": { "_id": null, "activity": { "$sum": 1 },
              "login_success": { "$sum": { "$cond": [{ "$and": [{ "$eq": ["$action", "login"] }, { "$eq": ["$status", "success"] }] }, 1, 0] } },
              "login_failure": { "$sum": { "$cond": [{ "$and": [{ "$eq": ["$action", "login"] }, { "$eq": ["$status", "failure"] }] }, 1, 0] } }
            } }
            """)
        };
        var result = await context.Logs.Aggregate<BsonDocument>(pipeline).SingleOrDefaultAsync(ct);
        return result is null ? new(0, 0, 0) : new(result["login_success"].ToInt64(), result["login_failure"].ToInt64(), result["activity"].ToInt64());
    }
    // Append one event without transactional promises about account operations.
    public Task AppendAsync(LogDocument log, CancellationToken ct) => context.Logs.InsertOneAsync(log, cancellationToken: ct);
    // Read a stable newest-first bounded page.
    public Task<List<LogDocument>> PageAsync(int limit, int skip, CancellationToken ct)
    {
        if (limit is < 1 or > 100 || skip < 0) throw new ApiException(400, "validation_error", "Invalid pagination.");
        return context.Logs.Find(FilterDefinition<LogDocument>.Empty).SortByDescending(x => x.Timestamp).ThenByDescending(x => x.Id).Skip(skip).Limit(limit)
            .Project<LogDocument>(Builders<LogDocument>.Projection.Include(x => x.Id).Include(x => x.UserId).Include(x => x.Action).Include(x => x.Status).Include(x => x.Timestamp)).ToListAsync(ct);
    }
    // Count events matching an optional time window and event kind in Mongo, not in memory.
    public Task<long> CountAsync(DateTime? since, string? action, string? status, CancellationToken ct)
    {
        var filter = Builders<LogDocument>.Filter.Empty;
        if (since.HasValue) filter &= Builders<LogDocument>.Filter.Gte(x => x.Timestamp, since.Value);
        if (action is not null) filter &= Builders<LogDocument>.Filter.Eq(x => x.Action, action);
        if (status is not null) filter &= Builders<LogDocument>.Filter.Eq(x => x.Status, status);
        return context.Logs.CountDocumentsAsync(filter, cancellationToken: ct);
    }
}
