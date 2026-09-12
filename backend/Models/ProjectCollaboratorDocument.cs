using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed record ProjectCollaboratorDocument
{
    [BsonElement("walletAddress")]
    public string WalletAddress { get; init; } = string.Empty;

    [BsonElement("scriptHash")]
    public string ScriptHash { get; init; } = string.Empty;

    [BsonElement("role")]
    public string Role { get; init; } = "deployer";

    [BsonElement("allowedNetworks")]
    public List<string> AllowedNetworks { get; init; } = [];

    [BsonElement("grantRevision")]
    public long GrantRevision { get; init; }

    [BsonElement("addedAtUtc")]
    public DateTime AddedAtUtc { get; init; }

    [BsonElement("addedByWalletAddress")]
    public string AddedByWalletAddress { get; init; } = string.Empty;

    [BsonElement("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; init; }

    [BsonElement("updatedByWalletAddress")]
    public string UpdatedByWalletAddress { get; init; } = string.Empty;
}
