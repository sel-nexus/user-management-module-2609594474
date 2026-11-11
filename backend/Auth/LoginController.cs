using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace UserManagement.Api.Auth;

// Expose only the approved public token issuance boundary.
[ApiController, Route("api/login")]
public sealed class LoginController(AuthenticationService authentication) : ControllerBase
{
    // Accept allowlisted credentials and return only the signed token and safe user projection.
    [HttpPost, AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest input, CancellationToken ct) => Ok(await authentication.LoginAsync(input, ct));
}
