using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

public sealed class WebhookDeliveryAttemptDocument
{
    [BsonId, BsonElement("_id")] public string Id { get; init; } = string.Empty;
    [BsonElement("deliveryId")] public string DeliveryId { get; init; } = string.Empty;
    [BsonElement("attemptNumber")] public int AttemptNumber { get; init; }
    [BsonElement("statusCode")] public int? StatusCode { get; init; }
    [BsonElement("succeeded")] public bool Succeeded { get; init; }
    [BsonElement("retryable")] public bool Retryable { get; init; }
    [BsonElement("error")] public string? Error { get; init; }
    [BsonElement("latencyMilliseconds")] public long LatencyMilliseconds { get; init; }
    [BsonElement("attemptedAt")] public DateTime AttemptedAt { get; init; }
}
