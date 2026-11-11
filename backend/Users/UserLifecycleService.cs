using MongoDB.Driver;
using UserManagement.Api.Auth;
using UserManagement.Api.Audit;
using UserManagement.Api.Common;
namespace UserManagement.Api.Users;

// Create only ordinary accounts; the unique Mongo index arbitrates concurrent requests.
public sealed class UserLifecycleService(IUserRepository users, PasswordHasher passwords, IAuditWriter audit)
{
    // Validate before persistence and return only the allowlisted public projection.
    public async Task<UserResponse> CreateAsync(CreateUserRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Username) || input.Username.Length > 64 || input.Username.Trim().Length < 3)
            throw new ApiException(400, "validation_error", "Username must be 3–64 characters.");
        if (input.Address?.Length > 256 || input.City?.Length > 100 || input.Country?.Length > 100 || input.Education?.Length > 100)
            throw new ApiException(400, "validation_error", "Address must be at most 256 characters; city, country and education at most 100.");
        passwords.Validate(input.Password);
        var now = DateTime.UtcNow;
        var user = new UserDocument
        {
            Username = input.Username.Trim().ToLowerInvariant(), PasswordHash = passwords.Hash(input.Password),
            Role = "User", IsActive = true, Address = input.Address, City = input.City,
            Country = input.Country, Education = input.Education, CreatedAt = now, UpdatedAt = now
        };
        try { await users.InsertAsync(user, ct); }
        catch (Exception e) when (e is ApiException or MongoException or TimeoutException)
        {
            await audit.WriteAsync(null, "create_user", "failure");
            throw;
        }
        await audit.WriteAsync(user.Id, "create_user", "success");
        return UserResponse.From(user);
    }
}
