using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

public sealed class WebhookSubscriptionDocument
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("projectId")]
    public string? ProjectId { get; init; }

    [BsonElement("name")]
    public string Name { get; init; } = string.Empty;

    [BsonElement("contractHash")]
    public string ContractHash { get; init; } = string.Empty;

    [BsonElement("eventName")]
    public string? EventName { get; init; }

    [BsonElement("webhookUrl")]
    public string WebhookUrl { get; init; } = string.Empty;

    [BsonElement("secret")]
    public string? Secret { get; init; }

    [BsonElement("headers")]
    public Dictionary<string, string> Headers { get; init; } = [];

    [BsonElement("isEnabled")]
    public bool IsEnabled { get; init; } = true;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}
