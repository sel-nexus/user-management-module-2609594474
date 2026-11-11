using System.ComponentModel.DataAnnotations;
using UserManagement.Api.Users;
using UserManagement.Api.Audit;
using UserManagement.Api.Common;
namespace UserManagement.Api.Auth;

public sealed record LoginRequest([Required, StringLength(64, MinimumLength = 3)] string Username, [Required] string Password);
public sealed record LoginResponse(string AccessToken, string TokenType, DateTime ExpiresAt, UserResponse User);

// Verify credentials against persisted accounts and audit truthful decisions safely.
public sealed class AuthenticationService(IUserRepository users, PasswordHasher passwords, TokenService tokens, IAuditWriter audit)
{
    private readonly string dummyHash = passwords.Hash(Guid.NewGuid().ToString("N"));
    // Normalize the username; use a dummy hash for missing accounts to reduce timing disclosure.
    public async Task<LoginResponse> LoginAsync(LoginRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Username) || input.Username.Trim().Length is < 3 or > 64)
            throw new ApiException(400, "validation_error", "Username must be 3–64 characters.");
        passwords.Validate(input.Password);
        var user = await users.FindByUsernameAsync(input.Username.Trim().ToLowerInvariant(), ct);
        var verified = passwords.Verify(input.Password, user?.PasswordHash ?? dummyHash);
        if (user is null || !verified || !user.IsActive || user.Role is not ("Admin" or "User"))
        {
            await audit.WriteAsync(user?.Id, "login", "failure");
            throw new ApiException(401, "invalid_credentials", "Username or password is incorrect.");
        }
        await audit.WriteAsync(user.Id, "login", "success");
        return tokens.Issue(user);
    }
}
