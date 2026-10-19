using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

// Init-only snapshots are copied into quotes/grants. Never resolve purchased terms
// through the current catalog. BSON field names are explicit for schema stability.
[BsonIgnoreExtraElements]
public sealed record RelayPackageTerms
{
    [BsonElement("packageId")] public string PackageId { get; init; } = "starter";
    [BsonElement("version")] public string Version { get; init; } = "launch-v1";
    [BsonElement("name")] public string Name { get; init; } = "Starter";
    [BsonElement("gasDatoshis")] public long GasDatoshis { get; init; } = 500000000;
    [BsonElement("days")] public int Days { get; init; } = 30;
    [BsonElement("graceDays")] public int GraceDays { get; init; } = 3;
    [BsonElement("maxActiveSubscriptions")] public int MaxActiveSubscriptions { get; init; } = 5;
    [BsonElement("maxEvents")] public int MaxEvents { get; init; } = 10000;
}

[BsonIgnoreExtraElements]
public sealed record RelayPurchaseTerms
{
    [BsonElement("schemaVersion")] public int SchemaVersion { get; init; } = 2;
    [BsonElement("operation")] public string Operation { get; init; } = "purchase_period";
    [BsonElement("package")] public RelayPackageTerms Package { get; init; } = new();
    [BsonElement("requiredGasDatoshis")] public long RequiredGasDatoshis { get; init; }
    [BsonElement("quotedAt")] public DateTime QuotedAt { get; init; }
    [BsonElement("source")] public string Source { get; init; } = "catalog";
    [BsonElement("targets")] public RelayPeriodTarget[] Targets { get; init; } = [];
    [BsonElement("bonus")] public RelayBonusTerms? Bonus { get; init; }
}

[BsonIgnoreExtraElements]
public sealed record RelayPeriodTarget
{
    [BsonElement("periodId")] public string PeriodId { get; init; } = "";
    [BsonElement("expectedVersion")] public long ExpectedVersion { get; init; }
    [BsonElement("originalTerms")] public RelayPackageTerms OriginalTerms { get; init; } = new();
    [BsonElement("targetTerms")] public RelayPackageTerms TargetTerms { get; init; } = new();
    [BsonElement("priceDatoshis")] public long PriceDatoshis { get; init; }
    [BsonElement("startsAt")] public DateTime StartsAt { get; init; }
    [BsonElement("endsAt")] public DateTime EndsAt { get; init; }
}

[BsonIgnoreExtraElements]
public sealed record RelayBonusTerms
{
    [BsonElement("policyVersion")] public string PolicyVersion { get; init; } = "current-starter-pro-v1";
    [BsonElement("target")] public RelayPeriodTarget Target { get; init; } = new() { PriceDatoshis = 0 };
}

[BsonIgnoreExtraElements]
public sealed record RelayPurchasedPeriodDocument
{
    [BsonId] public string Id { get; init; } = "";
    [BsonElement("projectId")] public string ProjectId { get; init; } = "";
    [BsonElement("purchaseId")] public string PurchaseId { get; init; } = "";
    [BsonElement("startsAt")] public DateTime StartsAt { get; init; }
    [BsonElement("endsAt")] public DateTime EndsAt { get; init; }
    [BsonElement("originalTerms")] public RelayPackageTerms OriginalTerms { get; init; } = new();
    [BsonElement("effectiveTerms")] public RelayPackageTerms EffectiveTerms { get; init; } = new();
    [BsonElement("version")] public long Version { get; init; }
    [BsonElement("mainnetLedgerId")] public string? MainnetLedgerId { get; init; }
    [BsonElement("testnetLedgerId")] public string? TestnetLedgerId { get; init; }
    [BsonElement("source")] public string Source { get; init; } = "purchase";
}

[BsonIgnoreExtraElements]
public sealed record RelayPeriodUpgradeDocument
{
    [BsonId] public string Id { get; init; } = "";
    [BsonElement("projectId")] public string ProjectId { get; init; } = "";
    [BsonElement("decisionId")] public string DecisionId { get; init; } = "";
    [BsonElement("target")] public RelayPeriodTarget Target { get; init; } = new();
    [BsonElement("isBonus")] public bool IsBonus { get; init; }
    [BsonElement("acceptedAt")] public DateTime AcceptedAt { get; init; }
}

[BsonIgnoreExtraElements]
public sealed record RelayBillingDecisionDocument
{
    [BsonId] public string Id { get; init; } = "";
    [BsonElement("projectId")] public string ProjectId { get; init; } = "";
    [BsonElement("intentId")] public string IntentId { get; init; } = "";
    [BsonElement("terms")] public RelayPurchaseTerms Terms { get; init; } = new();
    [BsonElement("acceptedAt")] public DateTime AcceptedAt { get; init; }
    [BsonElement("allocations")] public RelayGrantAllocation[] Allocations { get; init; } = [];
}

[BsonIgnoreExtraElements]
public sealed record RelayGrantAllocation
{
    [BsonElement("id")] public string Id { get; init; } = "";
    [BsonElement("periodId")] public string PeriodId { get; init; } = "";
    [BsonElement("kind")] public string Kind { get; init; } = "purchase";
    [BsonElement("status")] public string Status { get; init; } = "accepted";
    [BsonElement("reason")] public string? Reason { get; init; }
    [BsonElement("priceDatoshis")] public long PriceDatoshis { get; init; }
    [BsonElement("terms")] public RelayPackageTerms Terms { get; init; } = new();
    [BsonElement("startsAt")] public DateTime StartsAt { get; init; }
    [BsonElement("endsAt")] public DateTime EndsAt { get; init; }
}

[BsonIgnoreExtraElements]
public sealed record RelayGrantProgressDocument
{
    [BsonId] public string Id { get; init; } = "";
    [BsonElement("decisionId")] public string DecisionId { get; init; } = "";
    [BsonElement("allocationId")] public string AllocationId { get; init; } = "";
    [BsonElement("appliedNetworks")] public string[] AppliedNetworks { get; init; } = [];
    [BsonElement("version")] public long Version { get; init; }
}

public sealed record RelayPeriodResponse(string Id, DateTime StartsAt, DateTime EndsAt,
    RelayPackageTerms OriginalTerms, RelayPackageTerms EffectiveTerms, long Version, string Source);

public sealed record RelayPackageMetadata(RelayPackageTerms Terms, bool PurchaseEnabled);
public sealed record RelayCatalogResponse(string Revision, string Fingerprint, string WebhookScope,
    string EventScope, IReadOnlyList<RelayPackageMetadata> Packages, bool BillingReady, string? BillingBlocker,
    string BonusPolicyVersion);
