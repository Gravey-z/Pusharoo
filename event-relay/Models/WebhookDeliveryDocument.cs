using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace Pusharoo.EventRelay.Models;

public sealed record WebhookDeliveryDocument
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("network")] public string Network { get; init; } = "";
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

    [BsonElement("payloadJson")]
    [JsonIgnore]
    public string? PayloadJson { get; set; }

    [BsonElement("payloadVersion")] public int PayloadVersion { get; init; } = 2;

    [BsonElement("idempotencyKey")]
    public string IdempotencyKey { get; init; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; init; } = "succeeded";

    [BsonElement("attemptCount")]
    public int AttemptCount { get; init; } = 1;

    [BsonElement("latencyMilliseconds")]
    public long? LatencyMilliseconds { get; init; }

    [BsonElement("nextAttemptAt")] public DateTime? NextAttemptAt { get; init; }
    [BsonElement("claimOwner"), JsonIgnore] public string? ClaimOwner { get; init; }
    [BsonElement("allowancePeriodId"), JsonIgnore] public string? AllowancePeriodId { get; init; }
    [BsonElement("allowanceCharged"), JsonIgnore] public bool AllowanceCharged { get; init; }
    [BsonElement("attempts"), JsonIgnore] public List<WebhookDeliveryAttemptDocument> Attempts { get; init; } = [];
    [BsonElement("auditPending"), JsonIgnore] public bool AuditPending { get; init; }
    [BsonElement("leaseUntil")] public DateTime? LeaseUntil { get; init; }
    [BsonElement("eventName")] public string EventName { get; init; } = string.Empty;
    [BsonElement("trigger")] public string Trigger { get; init; } = "automatic";
    [BsonElement("redeliveryOfDeliveryId")] public string? RedeliveryOfDeliveryId { get; init; }
}
