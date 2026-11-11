using System.Globalization;
using Microsoft.AspNetCore.Http;
using UserManagement.Api.Common;
namespace UserManagement.Api.Discovery;

// Parse only scalar, approved query fields; duplicate and unknown keys fail closed.
public sealed record UserListQuery(int Limit = 20, int Skip = 0, string? Search = null, string? Country = null, string? Education = null, bool? IsActive = null, string Sort = "username", string Direction = "asc")
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal) { "limit", "skip", "search", "country", "education", "is_active", "sort", "direction" };
    // Reject malformed numbers and booleans before reaching persistence.
    public static UserListQuery Parse(IQueryCollection values)
    {
        foreach (var pair in values)
            if (!Keys.Contains(pair.Key) || pair.Value.Count != 1) throw Invalid();
        string? Value(string key) => values.TryGetValue(key, out var value) ? value[0] : null;
        int Number(string key, int fallback)
        {
            var value = Value(key);
            if (value is null) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Invalid();
            return number;
        }
        var active = Value("is_active");
        if (active is not null && active != "true" && active != "false") throw Invalid();
        var query = new UserListQuery(Number("limit", 20), Number("skip", 0), Value("search"), Value("country"), Value("education"), active is null ? null : active == "true", Value("sort") ?? "username", Value("direction") ?? "asc");
        query.Validate(); return query;
    }
    // Also enforce bounds for non-HTTP callers so Mongo never sees an unlimited limit.
    public void Validate()
    {
        if (Limit < 1 || Limit > 100 || Skip < 0 || Search?.Length > 100 || Country?.Length > 100 || Education?.Length > 100 ||
            Sort is not ("username" or "city" or "country" or "education") || Direction is not ("asc" or "desc")) throw Invalid();
    }
    private static ApiException Invalid() => new(400, "validation_error", "Use valid, single approved user query values.");
}
