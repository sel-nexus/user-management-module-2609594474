using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace UserManagement.Api.Discovery;

// Keep discovery authorization separate from lifecycle actions on the same resource.
[ApiController]
[Route("api/users")]
[Authorize(Roles = "Admin")]
public sealed class DiscoveryController(DiscoveryService discovery) : ControllerBase
{
    // Parse the entire query explicitly rather than accepting unknown model-binding keys.
    [HttpGet]
    public async Task<ActionResult<UserListResponse>> List(CancellationToken ct) =>
        Ok(await discovery.ListAsync(UserListQuery.Parse(Request.Query), ct));
}
