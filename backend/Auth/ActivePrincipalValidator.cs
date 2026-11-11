using Microsoft.AspNetCore.Authentication.JwtBearer;
using MongoDB.Bson;
using UserManagement.Api.Users;
namespace UserManagement.Api.Auth;

// Re-read account state for every protected request so stale activity/role claims fail closed.
public sealed class ActivePrincipalValidator(IUserRepository users)
{
    // Reject missing, invalid, inactive, deleted or role-mismatched persisted identities.
    public async Task ValidateAsync(TokenValidatedContext context)
    {
        if (!ObjectId.TryParse(context.Principal?.FindFirst("sub")?.Value, out var id)) { context.Fail("Invalid identity."); return; }
        var user = await users.FindByIdAsync(id, context.HttpContext.RequestAborted);
        if (user is null || !user.IsActive || user.Role is not ("Admin" or "User") || user.Role != context.Principal?.FindFirst("role")?.Value)
            context.Fail("Invalid account state.");
    }
}
