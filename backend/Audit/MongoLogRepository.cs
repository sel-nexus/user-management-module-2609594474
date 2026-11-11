using MongoDB.Driver;
using UserManagement.Api.Common;
namespace UserManagement.Api.Audit;

// Append and retrieve safe events through real Mongo storage, with bounded reads.
public sealed class MongoLogRepository(MongoContext context)
{
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
