using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using UserManagement.Api.Common;
namespace UserManagement.Api.Profile;

// Omitted optional fields remain untouched; explicit null clears only allowlisted text.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProfileRequest(JsonElement Address = default, JsonElement City = default, JsonElement Country = default, JsonElement Education = default)
{
    // Validate the whole patch before issuing any database write.
    public IReadOnlyDictionary<string, string?> Changes()
    {
        var changes = new Dictionary<string, string?>();
        foreach (var (name, value) in new[] { ("address", Address), ("city", City), ("country", Country), ("education", Education) })
        {
            if (value.ValueKind == JsonValueKind.Undefined) continue;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new ApiException(400, "validation_error", "Use text or null for optional profile fields.");
            var text = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            if (text?.Length > (name == "address" ? 256 : 100))
                throw new ApiException(400, "validation_error", "Profile field exceeds its maximum length.");
            changes[name] = text;
        }
        return changes;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangePasswordRequest([Required] string CurrentPassword, [Required] string NewPassword);
