using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace UserManagement.Api.Users;

// Persist one account; audit records reference its ObjectId rather than embedding credentials.
public sealed class UserDocument
{
    [BsonId] public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    [BsonElement("username")] public string Username { get; set; } = "";
    [BsonElement("password_hash")] public string PasswordHash { get; set; } = "";
    [BsonElement("role")] public string Role { get; set; } = "User";
    [BsonElement("is_active")] public bool IsActive { get; set; } = true;
    [BsonElement("address")] public string? Address { get; set; }
    [BsonElement("city")] public string? City { get; set; }
    [BsonElement("country")] public string? Country { get; set; }
    [BsonElement("education")] public string? Education { get; set; }
    private DateTime createdAt = CanonicalizeTimestamp(DateTime.UtcNow);
    private DateTime updatedAt = CanonicalizeTimestamp(DateTime.UtcNow);
    [BsonElement("created_at")] public DateTime CreatedAt { get => createdAt; set => createdAt = CanonicalizeTimestamp(value); }
    [BsonElement("updated_at")] public DateTime UpdatedAt { get => updatedAt; set => updatedAt = CanonicalizeTimestamp(value); }

    // Match BSON's UTC epoch-millisecond representation before persistence or public projection.
    public static DateTime CanonicalizeTimestamp(DateTime value) =>
        BsonUtils.ToDateTimeFromMillisecondsSinceEpoch(BsonUtils.ToMillisecondsSinceEpoch(value));
}
