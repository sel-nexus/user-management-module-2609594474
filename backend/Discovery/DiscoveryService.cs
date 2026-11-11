using UserManagement.Api.Users;
namespace UserManagement.Api.Discovery;

// Orchestrate bounded discovery and expose only the safe user projection.
public sealed class DiscoveryService(IUserRepository users)
{
    // Validate even direct callers; database failures propagate to shared ProblemDetails middleware.
    public async Task<UserListResponse> ListAsync(UserListQuery query, CancellationToken ct)
    {
        query.Validate();
        return await users.ListAsync(query, ct);
    }
}
public sealed record UserListResponse(IReadOnlyList<UserResponse> Items, int Limit, int Skip, long Total);
