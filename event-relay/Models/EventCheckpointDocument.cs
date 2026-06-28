using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

public sealed class EventCheckpointDocument
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("nextBlock")]
    public uint NextBlock { get; init; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}
