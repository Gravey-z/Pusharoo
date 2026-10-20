using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

public sealed record ReplayJobDocument
{
    [BsonId]
    public string Id { get; init; } = "";

    [BsonElement("projectId")]
    public string ProjectId { get; init; } = "";

    [BsonElement("subscriptionId")]
    public string SubscriptionId { get; init; } = "";

    [BsonElement("network")]
    public string Network { get; init; } = "";

    [BsonElement("contractHash")]
    public string ContractHash { get; init; } = "";

    [BsonElement("eventName")]
    public string? EventName { get; init; }

    [BsonElement("selectedEventNames")]
    public string[]? SelectedEventNames { get; init; }

    [BsonElement("targets")]
    public ReplayTarget[]? Targets { get; init; }

    [BsonElement("fromBlock")]
    public uint FromBlock { get; init; }

    [BsonElement("toBlock")]
    public uint ToBlock { get; init; }

    [BsonElement("nextBlock")]
    public uint NextBlock { get; init; }

    [BsonElement("status")]
    public string Status { get; init; } = "running";

    [BsonElement("active"), JsonIgnore]
    public bool Active { get; init; } = true;

    [BsonElement("owner"), JsonIgnore]
    public string? Owner { get; init; }

    [BsonElement("leaseUntil"), JsonIgnore]
    public DateTime? LeaseUntil { get; init; }

    [BsonElement("nextAttemptAt"), JsonIgnore]
    public DateTime NextAttemptAt { get; init; } = DateTime.UtcNow;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    [BsonElement("error")]
    public ScannerBlockingError? Error { get; init; }

    [BsonIgnore]
    public long CompletedBlocks => (long)NextBlock - FromBlock;

    [BsonIgnore]
    public long TotalBlocks => (long)ToBlock - FromBlock + 1;
}

public sealed record CreateReplayRequest(
    [property: JsonRequired] uint FromBlock,
    [property: JsonRequired] uint ToBlock
);

public sealed record ReplayTarget(
    [property: BsonElement("subscriptionId")] string SubscriptionId,
    [property: BsonElement("eventName")] string? EventName
);

public sealed record CreateProjectReplayRequest(
    [property: JsonRequired] uint FromBlock,
    [property: JsonRequired] uint ToBlock,
    [property: JsonRequired] string? ContractHash,
    [property: JsonRequired] string[]? EventNames
);
