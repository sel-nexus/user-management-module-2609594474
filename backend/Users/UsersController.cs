using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagement.Api.Common;
namespace UserManagement.Api.Users;

// Keep authorization and HTTP shaping at the boundary, with no direct database access.
[ApiController]
[Route("api/users")]
[Authorize(Roles = "Admin")]
public sealed class UsersController(UserLifecycleService lifecycle) : ControllerBase
{
    // Accept only the create DTO; identity, role, activity and credentials are never mass assigned.
    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create([FromBody] CreateUserRequest input, CancellationToken ct)
    {
        if (Request.Query.Count > 0) throw new ApiException(400, "validation_error", "Unknown query parameter.");
        var user = await lifecycle.CreateAsync(input, ct);
        return Created($"/api/users/{user.Id}", user);
    }
}
