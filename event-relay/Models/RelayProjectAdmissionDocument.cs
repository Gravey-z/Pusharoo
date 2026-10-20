using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

// One atomic admission authority for both networks. Active receipts are bounded
// by the package ceiling and are archived before a period can be replaced.
public sealed record RelayProjectAdmissionDocument
{
    [BsonId] public string Id { get; init; } = "";
    [BsonElement("version")] public long Version { get; init; }
    [BsonElement("mainnet")] public RelayNetworkBudget? Mainnet { get; init; }
    [BsonElement("testnet")] public RelayNetworkBudget? Testnet { get; init; }
    [BsonElement("slots")] public RelaySubscriptionSlot[] Slots { get; init; } = [];
    [BsonElement("transition")] public RelayPeriodTransition? Transition { get; init; }
    [BsonElement("mutation")] public RelaySubscriptionMutation? Mutation { get; init; }
    [BsonElement("publication")] public RelayPeriodPublication? Publication { get; init; }
    [BsonElement("selections")] public RelayDowngradeSelection[] Selections { get; init; } = [];
    [BsonElement("lastTransition")] public RelayTransitionOutcome? LastTransition { get; init; }
    [BsonElement("nextCheckAt")] public DateTime NextCheckAt { get; init; }
    [BsonElement("attemptAdmissions")] public long AttemptAdmissions { get; init; }
    [BsonElement("validUntil")] public DateTime? ValidUntil { get; init; }
}

public sealed record RelayNetworkBudget
{
    [BsonElement("entitlement")] public RelayEntitlementDocument Entitlement { get; init; } = new();
    [BsonElement("periodId")] public string PeriodId { get; init; } = "";
    [BsonElement("eventsUsed")] public int EventsUsed { get; init; }
    [BsonElement("receipts")] public string[] Receipts { get; init; } = [];
}

public sealed record RelaySubscriptionSlot(string Id, string Network, DateTime CreatedAt);
public sealed record RelayDowngradeSelection(string PeriodId, string[] SubscriptionIds, DateTime RecordedAt);
public sealed record RelayTransitionOutcome(string Id, DateTime AppliedAt, string[] PausedSubscriptionIds);

public sealed record RelayPeriodTransition
{
    public string Id { get; init; } = "";
    public long Revision { get; init; }
    public RelayEntitlementDocument Mainnet { get; init; } = new();
    public RelayEntitlementDocument Testnet { get; init; } = new();
    public RelaySubscriptionSlot[] Slots { get; init; } = [];
    public string[] PausedIds { get; init; } = [];
    public DateTime AppliedAt { get; init; }
    public DateTime? ValidUntil { get; init; }
}

public sealed record RelaySubscriptionMutation
{
    public string Id { get; init; } = "";
    public long Revision { get; init; }
    public WebhookSubscriptionDocument Subscription { get; init; } = new();
    public bool Delete { get; init; }
}

public sealed record RelayPeriodPublication(string Id, RelayBillingDecisionDocument Decision, RelayGrantAllocation Allocation);

public sealed record SetRelayDowngradeSelectionRequest(string PeriodId, string[] SubscriptionIds);
public sealed record RelayTransitionResponse(IReadOnlyList<RelayDowngradeSelection> Selections, RelayTransitionOutcome? LastTransition);
