using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

[BsonIgnoreExtraElements]
public sealed record ProjectAuthorizedDeployerDocument
{
    [BsonElement("walletAddress")]
    public string WalletAddress { get; init; } = string.Empty;

    [BsonElement("scriptHash")]
    public string ScriptHash { get; init; } = string.Empty;

    [BsonElement("allowedNetworks")]
    public List<string> AllowedNetworks { get; init; } = [];

    [BsonElement("addedAtUtc")]
    public DateTime AddedAtUtc { get; init; }

    [BsonElement("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; init; }
}
