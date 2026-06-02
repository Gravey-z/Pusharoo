using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed class ArtifactDocument
{
    [BsonId]
    [BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [BsonElement("version")]
    public string Version { get; init; } = string.Empty;

    [BsonElement("notes")]
    public string? Notes { get; init; }

    [BsonElement("contractName")]
    public string ContractName { get; init; } = string.Empty;

    [BsonElement("nefFileName")]
    public string NefFileName { get; init; } = string.Empty;

    [BsonElement("nefSize")]
    public long NefSize { get; init; }

    [BsonElement("nef")]
    public byte[] Nef { get; init; } = [];

    [BsonElement("manifest")]
    public NeoContractManifest Manifest { get; init; } = new();

    [BsonElement("summary")]
    public ArtifactSummary Summary { get; init; } = new();

    [BsonElement("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }
}
