using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed record DeploymentRecoveryEvidence
{
    [BsonElement("kind")]
    public string Kind { get; init; } = "unrecorded-deploy";

    [BsonElement("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [BsonElement("contractId")]
    public string ContractId { get; init; } = string.Empty;

    [BsonElement("network")]
    public string Network { get; init; } = string.Empty;

    [BsonElement("transactionId")]
    public string TransactionId { get; init; } = string.Empty;

    [BsonElement("walletAddress")]
    public string WalletAddress { get; init; } = string.Empty;

    [BsonElement("signerScriptHash")]
    public string SignerScriptHash { get; init; } = string.Empty;

    [BsonElement("requestedAtUtc")]
    public DateTime RequestedAtUtc { get; init; }

    [BsonElement("verifiedAtUtc")]
    [BsonIgnoreIfNull]
    public DateTime? VerifiedAtUtc { get; init; }

    [BsonElement("contractHash")]
    [BsonIgnoreIfNull]
    public string? ContractHash { get; init; }
}
