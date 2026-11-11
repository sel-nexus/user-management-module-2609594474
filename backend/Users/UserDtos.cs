using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using UserManagement.Api.Common;
namespace UserManagement.Api.Users;

// Define safe wire projections and allowlisted future account input, never entity serialization.
public sealed record UserResponse(string Id, string Username, string Role, bool IsActive, string? Address, string? City, string? Country, string? Education, DateTime CreatedAt, DateTime UpdatedAt)
{
    // Project only public fields from the persisted account.
    public static UserResponse From(UserDocument user) => new(user.Id.ToString(), user.Username, user.Role, user.IsActive, user.Address, user.City, user.Country, user.Education, user.CreatedAt, user.UpdatedAt);
}
public sealed record CreateUserRequest([Required, StringLength(64, MinimumLength = 3)] string Username, [Required] string Password, [StringLength(256)] string? Address = null, [StringLength(100)] string? City = null, [StringLength(100)] string? Country = null, [StringLength(100)] string? Education = null);
// Preserve omitted fields separately from explicit null, which clears optional profile fields.
public sealed record UpdateUserRequest(JsonElement Username = default, JsonElement Address = default, JsonElement City = default, JsonElement Country = default, JsonElement Education = default)
{
    // Validate all supplied scalars before any database operation; never accept operator objects.
    public IReadOnlyDictionary<string, string?> Changes()
    {
        var result = new Dictionary<string, string?>();
        foreach (var (name, value) in new[] { ("username", Username), ("address", Address), ("city", City), ("country", Country), ("education", Education) })
        {
            if (value.ValueKind == JsonValueKind.Undefined) continue;
            if (value.ValueKind != JsonValueKind.String && value.ValueKind != JsonValueKind.Null)
                throw new ApiException(400, "validation_error", "Use text or null for optional profile fields.");
            var text = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            if (name == "username")
            {
                if (string.IsNullOrWhiteSpace(text) || text.Length > 64 || text.Trim().Length < 3)
                    throw new ApiException(400, "validation_error", "Username must be 3–64 characters.");
                text = text.Trim().ToLowerInvariant();
            }
            else if (text?.Length > (name == "address" ? 256 : 100))
                throw new ApiException(400, "validation_error", "Profile field exceeds its maximum length.");
            result[name] = text;
        }
        return result;
    }
}
