using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed class WebhookAuthorizationNonceDocument
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("expiresAt")]
    public DateTime ExpiresAt { get; init; }
}
