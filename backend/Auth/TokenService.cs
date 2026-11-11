using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UserManagement.Api.Users;
namespace UserManagement.Api.Auth;

// Issue and validate the same configured short-lived signed token contract.
public sealed class TokenService
{
    private readonly string issuer;
    private readonly string audience;
    private readonly SymmetricSecurityKey key;
    public TokenService(IConfiguration config)
    {
        var secret = config["Jwt:Secret"] ?? "";
        issuer = config["Jwt:Issuer"] ?? "";
        audience = config["Jwt:Audience"] ?? "";
        if (Encoding.UTF8.GetByteCount(secret) < 64 || secret.Contains("change-me", StringComparison.OrdinalIgnoreCase) || secret.Contains("dev-secret", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) || config.GetValue<int>("Jwt:ExpiryMinutes") != 30)
            throw new InvalidOperationException("Configure a random Jwt__Secret of at least 64 bytes, issuer, audience and 30-minute expiry.");
        key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }
    // Create validation parameters; require HS256, signature, issuer, audience and exact lifetime.
    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = key, ValidateLifetime = true,
        RequireExpirationTime = true, RequireSignedTokens = true, ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], NameClaimType = "sub", RoleClaimType = "role"
    };
    // Issue the credential only after an active-account decision.
    public LoginResponse Issue(UserDocument user)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(30);
        var token = new JwtSecurityToken(issuer, audience,
            [new Claim("sub", user.Id.ToString()), new Claim("role", user.Role), new Claim("jti", Guid.NewGuid().ToString("N"))],
            now, expires, new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", expires, UserResponse.From(user));
    }
}
