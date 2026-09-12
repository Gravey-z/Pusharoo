using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

/// <summary>
/// An immutable access-history entry stored with its project. Collaborator changes
/// and this event are appended by the same MongoDB document update.
/// </summary>
public sealed record ProjectAccessAuditEvent
{
    [BsonElement("id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("action")]
    public string Action { get; init; } = string.Empty;

    [BsonElement("actorWalletAddress")]
    public string ActorWalletAddress { get; init; } = string.Empty;

    [BsonElement("targetWalletAddress")]
    public string TargetWalletAddress { get; init; } = string.Empty;

    [BsonElement("beforeRole")]
    [BsonIgnoreIfNull]
    public string? BeforeRole { get; init; }

    [BsonElement("afterRole")]
    [BsonIgnoreIfNull]
    public string? AfterRole { get; init; }

    [BsonElement("beforeNetworks")]
    public List<string> BeforeNetworks { get; init; } = [];

    [BsonElement("afterNetworks")]
    public List<string> AfterNetworks { get; init; } = [];

    [BsonElement("grantRevision")]
    public long GrantRevision { get; init; }

    [BsonElement("requestNonceHash")]
    public string RequestNonceHash { get; init; } = string.Empty;

    [BsonElement("createdAtUtc")]
    public DateTime CreatedAtUtc { get; init; }
}
