using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

// One bounded receipt set per allowance period; retained independently of delivery history.

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
