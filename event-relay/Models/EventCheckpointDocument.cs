using MongoDB.Bson.Serialization.Attributes;
namespace Pusharoo.EventRelay.Models;

public sealed class EventCheckpointDocument
{
    [BsonId] public string Id { get; init; } = string.Empty;
    // Null is a new checkpoint that has not yet chosen its starting height. Existing numeric checkpoints remain valid.
    [BsonElement("nextBlock")] public uint? NextBlock { get; init; }
    [BsonElement("networkTip")] public uint? NetworkTip { get; init; }
    [BsonElement("confirmedTip")] public uint? ConfirmedTip { get; init; }
    [BsonElement("lastPollAt")] public DateTime? LastPollAt { get; init; }
    [BsonElement("lastSuccessfulScanAt")] public DateTime? LastSuccessfulScanAt { get; init; }
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; init; }
    [BsonElement("scannerOwner")] public string? ScannerOwner { get; init; }
    [BsonElement("scannerLeaseUntil")] public DateTime? ScannerLeaseUntil { get; init; }
    [BsonElement("blockingError")] public ScannerBlockingError? BlockingError { get; init; }
}

public sealed record ScannerBlockingError(
    [property: BsonElement("blockIndex")] uint? BlockIndex,
    [property: BsonElement("transactionHash")] string? TransactionHash,
    [property: BsonElement("endpoint")] string Endpoint,
    [property: BsonElement("kind")] string Kind,
    [property: BsonElement("message")] string Message,
    [property: BsonElement("occurredAt")] DateTime OccurredAt);
