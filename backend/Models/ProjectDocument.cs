using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

[BsonIgnoreExtraElements]
public sealed class ProjectDocument
{
    public const string AuthorizedDeployersStorageField = "collaborators";

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

    [BsonElement("createdByWalletPublicKey")]
    public string? CreatedByWalletPublicKey { get; init; }

    [BsonElement("creatorNetwork")]
    public string? CreatorNetwork { get; init; }

    // Keep the existing storage field during the terminology migration so current
    // project records do not need a database migration.
    [BsonElement(AuthorizedDeployersStorageField)]
    public List<ProjectAuthorizedDeployerDocument> AuthorizedDeployers { get; init; } = [];

    [BsonElement("ownershipStatus")]
    [BsonIgnoreIfNull]
    public string? OwnershipStatus { get; init; }

    [BsonElement("ownershipAuditedAtUtc")]
    [BsonIgnoreIfNull]
    public DateTime? OwnershipAuditedAtUtc { get; init; }

    [BsonElement("idempotencyKey")]
    [BsonIgnoreIfNull]
    public string? IdempotencyKey { get; init; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }
}
