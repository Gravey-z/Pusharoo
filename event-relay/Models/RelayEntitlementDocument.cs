using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

public sealed class RelayEntitlementDocument
{
    [BsonId, BsonElement("_id")] public string Id { get; init; } = string.Empty;
    [BsonElement("projectId")] public string ProjectId { get; init; } = string.Empty;
    [BsonElement("network")] public string Network { get; init; } = string.Empty;
    [BsonElement("plan")] public string Plan { get; init; } = "free_beta";
    [BsonElement("status")] public string Status { get; init; } = "active";
    [BsonElement("periodStart")] public DateTime PeriodStart { get; init; }
    [BsonElement("periodEndsAt")] public DateTime PeriodEndsAt { get; init; }
    [BsonElement("maxActiveSubscriptions")] public int MaxActiveSubscriptions { get; init; }
    [BsonElement("maxEvents")] public int MaxEvents { get; init; }
    [BsonElement("eventsUsed")] public int EventsUsed { get; init; }
    [BsonElement("paidUntil")] public DateTime? PaidUntil { get; init; }
    [BsonElement("graceEndsAt")] public DateTime? GraceEndsAt { get; init; }
    [BsonElement("lastPaymentId")] public string? LastPaymentId { get; init; }
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; init; }
}
