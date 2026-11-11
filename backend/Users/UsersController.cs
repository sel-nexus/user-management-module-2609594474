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
    // Read only an administrator-selected account, never serialize the persistence document.
    [HttpGet("{id}")]
    public async Task<ActionResult<UserResponse>> Get(string id, CancellationToken ct)
    {
        RejectQuery();
        return Ok(await lifecycle.GetAsync(id, ct));
    }
    // Bind only approved fields; the DTO distinguishes missing values from explicit profile clears.
    [HttpPut("{id}")]
    public async Task<ActionResult<UserResponse>> Update(string id, [FromBody] UpdateUserRequest input, CancellationToken ct)
    {
        RejectQuery();
        return Ok(await lifecycle.UpdateAsync(id, input, ct));
    }
    // Soft-deactivate without deleting the persisted document.
    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(string id, CancellationToken ct)
    {
        RejectQuery();
        await lifecycle.DeactivateAsync(id, ct);
        return NoContent();
    }
    // Reject query pollution on targeted account endpoints.
    private void RejectQuery()
    {
        if (Request.Query.Count > 0) throw new ApiException(400, "validation_error", "Unknown query parameter.");
    }
    // Accept only the create DTO; identity, role, activity and credentials are never mass assigned.
    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create([FromBody] CreateUserRequest input, CancellationToken ct)
    {
        if (Request.Query.Count > 0) throw new ApiException(400, "validation_error", "Unknown query parameter.");
        var user = await lifecycle.CreateAsync(input, ct);
        return Created($"/api/users/{user.Id}", user);
    }
}
