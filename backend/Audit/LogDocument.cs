using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace UserManagement.Api.Audit;

// Persist only an optional identity and safe event metadata; no supplied usernames or secrets.
public sealed class LogDocument
{
    [BsonId] public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    [BsonElement("user_id"), BsonIgnoreIfNull] public ObjectId? UserId { get; set; }
    [BsonElement("action")] public string Action { get; set; } = "";
    [BsonElement("status")] public string Status { get; set; } = "";
    [BsonElement("timestamp")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
