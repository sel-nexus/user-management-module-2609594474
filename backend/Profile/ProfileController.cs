using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagement.Api.Common;
using UserManagement.Api.Users;
namespace UserManagement.Api.Profile;

// JWT validation supplies the sole target; all query pollution is rejected.
[ApiController]
[Route("api/profile")]
[Authorize(Roles = "Admin,User")]
public sealed class ProfileController(ProfileService profiles) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<UserResponse>> Get(CancellationToken ct)
    { RejectQuery(); return Ok(await profiles.GetAsync(User, ct)); }

    [HttpPut]
    public async Task<ActionResult<UserResponse>> Update([FromBody] UpdateProfileRequest input, CancellationToken ct)
    { RejectQuery(); return Ok(await profiles.UpdateAsync(User, input, ct)); }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest input, CancellationToken ct)
    { RejectQuery(); await profiles.ChangePasswordAsync(User, input, ct); return NoContent(); }

    private void RejectQuery()
    { if (Request.Query.Count > 0) throw new ApiException(400, "validation_error", "Unknown query parameter."); }
}
