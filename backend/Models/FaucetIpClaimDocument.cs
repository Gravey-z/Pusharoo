using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed class FaucetIpClaimDocument
{
    [BsonId]
    public string IpAddress { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
