using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace UserManagement.Api.Common;

// Provide real Mongo-backed readiness through the public infrastructure route.
[ApiController, Route("api/health")]
public sealed class HealthController(MongoContext context) : ControllerBase
{
    // Return healthy only when the configured database answers.
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct) { await context.PingAsync(ct); return Ok(new { status = "healthy" }); }
}
