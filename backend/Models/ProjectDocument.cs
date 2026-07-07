using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed class ProjectDocument
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("name")]
    public string Name { get; init; } = string.Empty;

    [BsonElement("description")]
    public string? Description { get; init; }

    [BsonElement("createdByWalletAddress")]
    public string? CreatedByWalletAddress { get; init; }

    [BsonElement("createdByWalletScriptHash")]
    public string? CreatedByWalletScriptHash { get; init; }

    [BsonElement("creatorNetwork")]
    public string? CreatorNetwork { get; init; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }
}
