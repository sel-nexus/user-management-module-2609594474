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
}
