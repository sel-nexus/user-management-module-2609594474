using System.Text;
using UserManagement.Api.Common;
namespace UserManagement.Api.Auth;

// Enforce bcrypt's byte boundary so no password is silently truncated.
public sealed class PasswordHasher
{
    // Validate UTF-8 bytes, not character count. Raises: safe validation error.
    public void Validate(string? password)
    {
        if (password is null || Encoding.UTF8.GetByteCount(password) is < 12 or > 72)
            throw new ApiException(400, "validation_error", "Password must be 12–72 UTF-8 bytes.");
    }
    // Produce a fresh bcrypt salt and work-factor-12 hash.
    public string Hash(string password) { Validate(password); return BCrypt.Net.BCrypt.HashPassword(password, 12); }
    // Fail closed on invalid input or damaged persisted hashes.
    public bool Verify(string? password, string hash)
    {
        if (password is null || Encoding.UTF8.GetByteCount(password) is < 12 or > 72) return false;
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch (BCrypt.Net.SaltParseException) { return false; }
    }
}
