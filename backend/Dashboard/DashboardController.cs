using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagement.Api.Common;
namespace UserManagement.Api.Dashboard;

// Authorize before query validation; return only the approved read-only summary.
[ApiController]
[Route("api/dashboard")]
[Authorize(Roles = "Admin")]
public sealed class DashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DashboardSummary>> Get(CancellationToken ct)
    {
        if (Request.Query.Count > 0) throw new ApiException(400, "validation_error", "Dashboard does not accept query parameters.");
        return Ok(await dashboard.GetAsync(ct));
    }
}
