using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace UserManagement.Api.Audit;

// Authorize before parsing, and never expose persistence documents directly.
[ApiController]
[Route("api/logs")]
[Authorize(Roles = "Admin")]
public sealed class LogsController(AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AuditPage>> List(CancellationToken ct) => Ok(await audit.ListAsync(AuditQuery.Parse(Request.Query), ct));
}
