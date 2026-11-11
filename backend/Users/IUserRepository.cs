using MongoDB.Bson;
using UserManagement.Api.Discovery;
namespace UserManagement.Api.Users;

// Expose the account persistence needed by authentication and explicit bootstrap.
public interface IUserRepository
{
    Task<UserDocument?> FindByUsernameAsync(string username, CancellationToken ct);
    Task<UserDocument?> FindByIdAsync(ObjectId id, CancellationToken ct);
    Task<UserListResponse> ListAsync(UserListQuery query, CancellationToken ct);
    Task InsertAsync(UserDocument user, CancellationToken ct);
    Task<UserDocument?> UpdateAsync(ObjectId id, IReadOnlyDictionary<string, string?> changes, CancellationToken ct);
    Task<UserDocument?> UpdateProfileAsync(ObjectId id, string role, IReadOnlyDictionary<string, string?> changes, CancellationToken ct);
    Task<bool> ChangePasswordAsync(ObjectId id, string role, string expectedHash, string newHash, CancellationToken ct);
    Task<UserDocument?> DeactivateAsync(ObjectId id, CancellationToken ct);
}
