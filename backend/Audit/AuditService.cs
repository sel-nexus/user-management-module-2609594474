using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using UserManagement.Api.Common;
namespace UserManagement.Api.Audit;

// Share the strict pagination contract between HTTP and service callers.
public sealed record AuditQuery(int Limit = 20, int Skip = 0)
{
    public static AuditQuery Parse(IQueryCollection values)
    {
        foreach (var pair in values)
            if (pair.Key is not ("limit" or "skip") || pair.Value.Count != 1) throw Invalid();
        int Number(string key, int fallback) => !values.TryGetValue(key, out var value) ? fallback
            : int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw Invalid();
        var query = new AuditQuery(Number("limit", 20), Number("skip", 0));
        query.Validate(); return query;
    }
    public void Validate() { if (Limit is < 1 or > 100 || Skip < 0) throw Invalid(); }
    private static ApiException Invalid() => new(400, "validation_error", "Use valid, single limit and skip values.");
}
public sealed record AuditRecord(string Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId, string Action, string Status, DateTime Timestamp);
public sealed record AuditPage(IReadOnlyList<AuditRecord> Items, int Limit, int Skip, long Total);

// Review is strictly read-only; audit delivery is not invoked by this service.
public sealed class AuditService(MongoLogRepository repository)
{
    public async Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct)
    {
        query.Validate();
        var total = await repository.CountAsync(null, null, null, ct);
        var items = await repository.PageAsync(query.Limit, query.Skip, ct);
        return new(items.Select(x => new AuditRecord(x.Id.ToString(), x.UserId?.ToString(), x.Action, x.Status, x.Timestamp)).ToArray(), query.Limit, query.Skip, total);
    }
}
