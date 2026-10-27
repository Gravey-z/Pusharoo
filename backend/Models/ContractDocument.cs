using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed record ContractDocument
{
    [BsonId]
    public string Id { get; init; } = string.Empty;

    [BsonElement("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [BsonElement("name")]
    public string Name { get; init; } = string.Empty;

    [BsonElement("normalizedName")]
    public string NormalizedName { get; init; } = string.Empty;

    [BsonElement("description")]
    public string? Description { get; init; }

    [BsonElement("testnetDeploymentWriteToken")]
    [BsonIgnoreIfNull]
    public string? TestnetDeploymentWriteToken { get; init; }

    [BsonElement("mainnetDeploymentWriteToken")]
    [BsonIgnoreIfNull]
    public string? MainnetDeploymentWriteToken { get; init; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [BsonElement("idempotencyKey")]
    [BsonIgnoreIfNull]
    public string? IdempotencyKey { get; init; }

    [BsonElement("idempotencyPayloadHash")]
    [BsonIgnoreIfNull]
    public string? IdempotencyPayloadHash { get; init; }
}
