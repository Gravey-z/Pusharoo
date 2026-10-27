using MongoDB.Bson;

namespace Pusharoo.Migration;

public sealed record MigrationDecisions
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<ProjectDecision> Projects { get; init; } = [];
}

public sealed record ProjectDecision
{
    public string ProjectId { get; init; } = "";
    public IReadOnlyList<ContractDecision> Contracts { get; init; } = [];
}

public sealed record ContractDecision
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? ExistingContractId { get; init; }
    public IReadOnlyList<string> ArtifactIds { get; init; } = [];
    public IReadOnlyList<string> DeploymentIds { get; init; } = [];
}

public sealed record MigrationIssue(
    string Code,
    string? ProjectId,
    string? RecordId,
    string Message
);

public sealed record ContractPlan(
    string Id,
    string ProjectId,
    string Name,
    DateTime CreatedAt,
    bool Existing
);

public sealed record RecordPlan(
    string Kind,
    string Id,
    string ProjectId,
    string ContractId,
    string? OriginalContractId,
    string SourceHash,
    bool AuditLegacy
);

public sealed record ProjectPlan(
    string ProjectId,
    string SourceHash,
    int OriginalSchemaVersion,
    IReadOnlyList<ContractPlan> Contracts,
    IReadOnlyList<RecordPlan> Records
);

public sealed record NetworkInventory(
    string Network,
    IReadOnlyList<string> ConfirmedHashes,
    int ActiveAttempts
);

public sealed record ProjectInventory(
    string Id,
    int SchemaVersion,
    string? MigrationState,
    int Artifacts,
    int Deployments,
    int Contracts,
    IReadOnlyList<NetworkInventory> Networks
);

public sealed record ArtifactInventory(
    string Id,
    string ProjectId,
    string? ContractId,
    string? Version
);

public sealed record DeploymentInventory(
    string Id,
    string ProjectId,
    string? ContractId,
    string? ArtifactId,
    string? Network,
    string? Status,
    string? TransactionId,
    string? ContractHash,
    string? ActiveAttemptKey
);

public sealed record ContractInventory(string Id, string ProjectId, string? Name);

public sealed record IndexInventory(string Collection, IReadOnlyList<BsonDocument> Indexes);

public sealed record MigrationReport(
    int Version,
    IReadOnlyList<ProjectInventory> Projects,
    IReadOnlyList<ProjectPlan> Plans,
    IReadOnlyList<MigrationIssue> Issues,
    IReadOnlyList<IndexInventory> Indexes
)
{
    public bool ReadyToApply => Issues.Count == 0;
    public IReadOnlyList<ArtifactInventory> Artifacts { get; init; } = [];
    public IReadOnlyList<DeploymentInventory> Deployments { get; init; } = [];
    public IReadOnlyList<ContractInventory> Contracts { get; init; } = [];
}

public sealed record MigrationApplyOptions(
    bool WritersStopped,
    bool BackupVerified,
    bool WritesDisabled
);

public sealed record MigrationResult(string Status, MigrationReport Report);

public sealed class MigrationBlockedException(string message) : Exception(message);

public sealed class MigrationSnapshot
{
    public IReadOnlyList<BsonDocument> Projects { get; init; } = [];
    public IReadOnlyList<BsonDocument> Contracts { get; init; } = [];
    public IReadOnlyList<BsonDocument> Artifacts { get; init; } = [];
    public IReadOnlyList<BsonDocument> Deployments { get; init; } = [];
    public IReadOnlyList<BsonDocument> Journals { get; init; } = [];
    public IReadOnlyList<IndexInventory> Indexes { get; init; } = [];
}
