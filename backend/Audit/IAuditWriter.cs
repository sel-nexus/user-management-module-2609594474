using MongoDB.Bson;
namespace UserManagement.Api.Audit;

// Report best-effort delivery separately from the business outcome.
public interface IAuditWriter
{
    Task WriteAsync(ObjectId? userId, string action, string status);
}
