namespace Pusharoo.EventRelay.Models;

public sealed record NeoBlock(uint Index, string Hash, DateTimeOffset Timestamp, IReadOnlyList<string> TransactionHashes);
