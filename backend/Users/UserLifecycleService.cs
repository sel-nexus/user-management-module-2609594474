using MongoDB.Driver;
using MongoDB.Bson;
using UserManagement.Api.Auth;
using UserManagement.Api.Audit;
using UserManagement.Api.Common;
namespace UserManagement.Api.Users;

// Create only ordinary accounts; the unique Mongo index arbitrates concurrent requests.
public sealed class UserLifecycleService(IUserRepository users, PasswordHasher passwords, IAuditWriter audit)
{
    // Read active or inactive records through a safe projection; audit the actual outcome.
    public Task<UserResponse> GetAsync(string id, CancellationToken ct) => ManageAsync(id, "read_user", key => users.FindByIdAsync(key, ct));
    // Apply strict partial updates with database-arbitrated uniqueness and last-write-wins semantics.
    public Task<UserResponse> UpdateAsync(string id, UpdateUserRequest input, CancellationToken ct) =>
        ManageAsync(id, "update_user", key => users.UpdateAsync(key, input.Changes(), ct));
    // Preserve the account document; repeated deactivation remains a successful idempotent operation.
    public async Task DeactivateAsync(string id, CancellationToken ct) =>
        _ = await ManageAsync(id, "deactivate_user", key => users.DeactivateAsync(key, ct));
    // Never report success for malformed, missing, invalid or failed persistence decisions.
    private async Task<UserResponse> ManageAsync(string id, string action, Func<ObjectId, Task<UserDocument?>> operation)
    {
        ObjectId? key = null;
        UserDocument user;
        try
        {
            if (id.Length != 24 || !ObjectId.TryParse(id, out var parsed))
                throw new ApiException(400, "validation_error", "Malformed account id.");
            key = parsed;
            user = await operation(parsed) ?? throw new ApiException(404, "user_not_found", "Account not found.");
        }
        catch (Exception e) when (e is ApiException or MongoException or TimeoutException or OperationCanceledException)
        {
            await audit.WriteAsync(key, action, "failure");
            throw;
        }
        await audit.WriteAsync(user.Id, action, "success");
        return UserResponse.From(user);
    }
    // Validate before persistence and return only the allowlisted public projection.
    public async Task<UserResponse> CreateAsync(CreateUserRequest input, CancellationToken ct)
    {
        // Reject invalid or already-cancelled requests before beginning an audited operation.
        if (string.IsNullOrWhiteSpace(input.Username) || input.Username.Length > 64 || input.Username.Trim().Length < 3)
            throw new ApiException(400, "validation_error", "Username must be 3–64 characters.");
        if (input.Address?.Length > 256 || input.City?.Length > 100 || input.Country?.Length > 100 || input.Education?.Length > 100)
            throw new ApiException(400, "validation_error", "Address must be at most 256 characters; city, country and education at most 100.");
        passwords.Validate(input.Password);
        ct.ThrowIfCancellationRequested();
        UserDocument user;
        try
        {
            var now = DateTime.UtcNow;
            user = new UserDocument
            {
                Username = input.Username.Trim().ToLowerInvariant(), PasswordHash = passwords.Hash(input.Password),
                Role = "User", IsActive = true, Address = input.Address, City = input.City,
                Country = input.Country, Education = input.Education, CreatedAt = now, UpdatedAt = now
            };
            await users.InsertAsync(user, ct);
        }
        catch (Exception e) when (e is ApiException or MongoException or TimeoutException or OperationCanceledException)
        {
            await audit.WriteAsync(null, "create_user", "failure");
            throw;
        }
        await audit.WriteAsync(user.Id, "create_user", "success");
        return UserResponse.From(user);
    }
}
