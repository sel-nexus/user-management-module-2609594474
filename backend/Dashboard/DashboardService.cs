using UserManagement.Api.Users;
using UserManagement.Api.Audit;
namespace UserManagement.Api.Dashboard;

// Safe aggregate records contain no account identities, credentials or individual audit events.
public sealed record CountryCount(string Country, long Count);
public sealed record PopulationSummary(long Total, long Active, long Inactive, IReadOnlyList<CountryCount> CountryDistribution);
public sealed record ActivitySummary(long LoginSuccess, long LoginFailure, long Activity);
public sealed record DashboardSummary(long Total, long Active, long Inactive, IReadOnlyList<CountryCount> CountryDistribution,
    long LoginSuccess, long LoginFailure, long Activity, DateTime GeneratedAt, DateTime WindowStart);

// Sample UTC once so all seven-day counts share the same inclusive boundaries and freshness.
public sealed class DashboardService(IUserRepository users, MongoLogRepository logs, TimeProvider clock)
{
    public async Task<DashboardSummary> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var generatedAt = clock.GetUtcNow().UtcDateTime;
        var windowStart = generatedAt.AddDays(-7);
        var population = await users.SummarizeAsync(ct);
        var activity = await logs.SummarizeAsync(windowStart, generatedAt, ct);
        return new(population.Total, population.Active, population.Inactive, population.CountryDistribution,
            activity.LoginSuccess, activity.LoginFailure, activity.Activity, generatedAt, windowStart);
    }
}
