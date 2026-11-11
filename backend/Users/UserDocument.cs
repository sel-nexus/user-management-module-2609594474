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
    [BsonElement("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
