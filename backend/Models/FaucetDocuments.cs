using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

[BsonIgnoreExtraElements]
public sealed class FaucetClaimDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string ScriptHash { get; set; } = string.Empty;
    public string State { get; set; } = "Queued";
    public string? ActiveWalletKey { get; set; }
    public string? TransactionHash { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? SignedTransaction { get; set; }
    public uint? ValidUntilBlock { get; set; }
    public int Attempts { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class FaucetDailyFeeBudgetDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public long ReservedDatoshis { get; set; }
    public Dictionary<string, long> Reservations { get; set; } = [];
}
