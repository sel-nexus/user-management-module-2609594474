using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace UserManagement.Tests;

// Expose test-only protected routes to exercise middleware without implementing later product features.
[ApiController, Route("api/auth-probe")]
public sealed class AuthProbeController : ControllerBase
{
    // Verify a valid active principal reaches a protected action.
    [HttpGet, Authorize] public IActionResult Get() => Ok(new { authenticated = true });
    // Verify the persisted Admin role gates protected actions.
    [HttpGet("admin"), Authorize(Roles = "Admin")] public IActionResult Admin() => Ok(new { authorized = true });
}
