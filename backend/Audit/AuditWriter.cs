using MongoDB.Bson;
namespace UserManagement.Api.Audit;

// Deliver synchronous best-effort audit with a bounded independent timeout and safe observability.
public sealed class AuditWriter(MongoLogRepository repository, ILogger<AuditWriter> logger) : IAuditWriter
{
    // Never fail the completed credential decision because audit delivery failed.
    public async Task WriteAsync(ObjectId? userId, string action, string status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await repository.AppendAsync(new LogDocument { UserId = userId, Action = action, Status = status }, timeout.Token); }
        catch (Exception e) { logger.LogWarning("Audit delivery failed: {FailureType}", e.GetType().Name); }
    }
}
