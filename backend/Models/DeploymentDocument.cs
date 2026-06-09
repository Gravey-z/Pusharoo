using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed class DeploymentDocument
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [BsonElement("artifactId")]
    public string ArtifactId { get; init; } = string.Empty;

    [BsonElement("version")]
    public string Version { get; init; } = string.Empty;

    [BsonElement("network")]
    public string Network { get; init; } = string.Empty;

    [BsonElement("contractHash")]
    public string? ContractHash { get; init; }

    [BsonElement("transactionId")]
    public string? TransactionId { get; init; }

    [BsonElement("deployedBy")]
    public string DeployedBy { get; init; } = string.Empty;

    [BsonElement("notes")]
    public string? Notes { get; init; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }
}
