using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

public sealed class RelayAllowancePeriodDocument
{
    [BsonId]
    public string Id { get; init; } = "";

    [BsonElement("closed")]
    public bool Closed { get; init; }

    [BsonElement("periodEndsAt")]
    public DateTime PeriodEndsAt { get; init; }

    [BsonElement("eventsUsed")]
    public int EventsUsed { get; init; }

    [BsonElement("chargedDeliveries")]
    public List<string> ChargedDeliveries { get; init; } = [];
}
