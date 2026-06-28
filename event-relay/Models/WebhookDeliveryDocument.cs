using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

public sealed class WebhookDeliveryDocument
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("subscriptionId")]
    public string SubscriptionId { get; init; } = string.Empty;

    [BsonElement("eventId")]
    public string EventId { get; init; } = string.Empty;

    [BsonElement("webhookUrl")]
    public string WebhookUrl { get; init; } = string.Empty;

    [BsonElement("statusCode")]
    public int? StatusCode { get; init; }

    [BsonElement("succeeded")]
    public bool Succeeded { get; init; }

    [BsonElement("error")]
    public string? Error { get; init; }

    [BsonElement("deliveredAt")]
    public DateTime DeliveredAt { get; init; }
}
