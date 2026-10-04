using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed class WalletLoginChallengeDocument
{
    [BsonId]
    public string Id { get; init; } = string.Empty;
    public string BrowserBindingHash { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string ScriptHash { get; init; } = string.Empty;
    public string Network { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Origin { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string Nonce { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTime IssuedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public DateTime? ConsumedAtUtc { get; init; }
}

public sealed class WalletLoginSessionDocument
{
    [BsonId]
    public string TokenHash { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string ScriptHash { get; init; } = string.Empty;
    public string PublicKey { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string NetworkAtLogin { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public DateTime? RevokedAtUtc { get; init; }
}
