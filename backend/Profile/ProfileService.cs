using System.Security.Claims;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api.Audit;
using UserManagement.Api.Auth;
using UserManagement.Api.Common;
using UserManagement.Api.Users;
namespace UserManagement.Api.Profile;

// Own-profile operations never accept a route, body or query identity.
public sealed class ProfileService(IUserRepository users, PasswordHasher passwords, IAuditWriter audit)
{
    // Reads do not generate activity or mutate the account.
    public async Task<UserResponse> GetAsync(ClaimsPrincipal principal, CancellationToken ct) =>
        UserResponse.From(await ActiveAsync(Identity(principal), principal, ct));

    // Recheck account state and atomically patch only approved fields.
    public async Task<UserResponse> UpdateAsync(ClaimsPrincipal principal, UpdateProfileRequest input, CancellationToken ct)
    {
        var id = Identity(principal);
        UserDocument saved;
        try
        {
            await ActiveAsync(id, principal, ct);
            saved = await users.UpdateProfileAsync(id, principal.FindFirst("role")!.Value, input.Changes(), ct)
                ?? throw InvalidIdentity();
        }
        catch (Exception e) when (e is ApiException or MongoException or TimeoutException or OperationCanceledException)
        { await audit.WriteAsync(id, "update_profile", "failure"); throw; }
        await audit.WriteAsync(id, "update_profile", "success");
        return UserResponse.From(saved);
    }

    // Compare-and-swap the verified hash so racing password changes cannot overwrite a winner.
    public async Task ChangePasswordAsync(ClaimsPrincipal principal, ChangePasswordRequest input, CancellationToken ct)
    {
        var id = Identity(principal);
        try
        {
            var user = await ActiveAsync(id, principal, ct);
            passwords.Validate(input.CurrentPassword);
            passwords.Validate(input.NewPassword);
            if (!passwords.Verify(input.CurrentPassword, user.PasswordHash))
                throw new ApiException(400, "invalid_current_password", "Current password is incorrect.");
            var hash = passwords.Hash(input.NewPassword);
            if (!await users.ChangePasswordAsync(id, user.Role, user.PasswordHash, hash, ct))
                throw new ApiException(400, "password_change_rejected", "Password change could not be completed. Check your current password and retry.");
        }
        catch (Exception e) when (e is ApiException or MongoException or TimeoutException or OperationCanceledException)
        { await audit.WriteAsync(id, "change_password", "failure"); throw; }
        await audit.WriteAsync(id, "change_password", "success");
    }

    // Only authenticated sub claims are usable; alternate identity claims are deliberately ignored.
    private static ObjectId Identity(ClaimsPrincipal principal)
    {
        var subjects = principal.FindAll("sub").ToArray();
        if (principal.Identity?.IsAuthenticated != true || subjects.Length != 1 || subjects[0].Value.Length != 24 || !ObjectId.TryParse(subjects[0].Value, out var id))
            throw InvalidIdentity();
        return id;
    }
    // Fail closed if state changed since JWT validation.
    private async Task<UserDocument> ActiveAsync(ObjectId id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id, ct);
        if (user is null || !user.IsActive || user.Role is not ("Admin" or "User") || user.Role != principal.FindFirst("role")?.Value)
            throw InvalidIdentity();
        return user;
    }
    private static ApiException InvalidIdentity() => new(401, "invalid_token", "Sign in with a valid active account.");
}
