using System.Text.Json;
using System.Text.RegularExpressions;
using backend.Models;
using backend.Services;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using static Pusharoo.Migration.MigrationIdentity;

namespace Pusharoo.Migration;

public sealed class MigrationPlanner
{
    public MigrationReport Build(MigrationSnapshot source, MigrationDecisions? decisions = null)
    {
        try
        {
            return BuildCore(source, decisions) with
            {
                Artifacts = source
                    .Artifacts.Select(item => new ArtifactInventory(
                        Id(item),
                        Parent(item),
                        Text(item, "contractId"),
                        Text(item, "version")
                    ))
                    .ToArray(),
                Deployments = source
                    .Deployments.Select(item => new DeploymentInventory(
                        Id(item),
                        Parent(item),
                        Text(item, "contractId"),
                        Text(item, "artifactId"),
                        Text(item, "network"),
                        Text(item, "status"),
                        Text(item, "transactionId"),
                        Text(item, "contractHash"),
                        Text(item, "activeAttemptKey")
                    ))
                    .ToArray(),
                Contracts = source
                    .Contracts.Select(item => new ContractInventory(
                        Id(item),
                        Parent(item),
                        Text(item, "name")
                    ))
                    .ToArray(),
            };
        }
        catch (Exception error)
            when (error
                    is FormatException
                        or InvalidCastException
                        or ArgumentException
                        or KeyNotFoundException
                        or JsonException
                        or BsonSerializationException
            )
        {
            var projects = source
                .Projects.Select(project => new ProjectInventory(
                    Id(project),
                    project.GetValue("contractSchemaVersion", 0).IsInt32
                        ? project.GetValue("contractSchemaVersion", 0).AsInt32
                        : -1,
                    Text(project, "contractMigrationState"),
                    source.Artifacts.Count(item => Parent(item) == Id(project)),
                    source.Deployments.Count(item => Parent(item) == Id(project)),
                    source.Contracts.Count(item => Parent(item) == Id(project)),
                    []
                ))
                .ToArray();
            return new(
                1,
                projects,
                [],
                [
                    new(
                        "unsupported_legacy_shape",
                        null,
                        null,
                        "A source document or established journal has an unsupported shape. Inspect the original BSON without changing historical evidence."
                    ),
                ],
                source.Indexes
            );
        }
    }

    private MigrationReport BuildCore(MigrationSnapshot source, MigrationDecisions? decisions)
    {
        decisions ??= new();
        var issues = new List<MigrationIssue>();
        var plans = new List<ProjectPlan>();
        var inventory = new List<ProjectInventory>();
        void Issue(string code, string? project, string? record, string message) =>
            issues.Add(new(code, project, record, message));
        if (decisions.Version != 1)
            Issue("unsupported_mapping_version", null, null, "Mapping version must be 1.");
        foreach (
            var duplicate in decisions
                .Projects.GroupBy(item => item.ProjectId)
                .Where(group => group.Count() > 1)
        )
            Issue(
                "duplicate_project_decision",
                duplicate.Key,
                null,
                "Supply exactly one mapping decision per project."
            );
        var projectIds = source.Projects.Select(Id).ToHashSet();
        foreach (var journal in source.Journals.Where(item => !projectIds.Contains(Id(item))))
            Issue(
                "journal_project_missing",
                Id(journal),
                null,
                "An established project mapping lost its original project; stop and inspect the source."
            );
        foreach (
            var decision in decisions.Projects.Where(item => !projectIds.Contains(item.ProjectId))
        )
            Issue(
                "unknown_project_decision",
                decision.ProjectId,
                null,
                "Mapping refers to a missing project."
            );
        foreach (var indexed in source.Indexes)
        {
            foreach (var index in indexed.Indexes)
            {
                var keys = index["key"].AsBsonDocument;
                var scopedVersion =
                    indexed.Collection == "contractArtifacts"
                    && keys.Equals(
                        new BsonDocument
                        {
                            { "projectId", 1 },
                            { "contractId", 1 },
                            { "version", 1 },
                        }
                    );
                var attemptIdentity =
                    indexed.Collection == "deployments"
                    && (
                        keys.Equals(new BsonDocument("transactionId", 1))
                        || keys.Equals(new BsonDocument("activeAttemptKey", 1))
                    );
                if (
                    (scopedVersion || attemptIdentity)
                    && (
                        !index.GetValue("unique", false).ToBoolean()
                        || index.GetValue("sparse", false).ToBoolean() != attemptIdentity
                        || index.Contains("partialFilterExpression")
                        || index.Contains("collation")
                    )
                )
                    Issue(
                        "incompatible_index",
                        null,
                        indexed.Collection + ":" + index["name"].AsString,
                        "An existing target index has incompatible uniqueness, sparsity, partial, or collation rules. Review it before migration."
                    );
            }
        }
        foreach (
            var pair in new[]
            {
                ("contract", source.Contracts),
                ("artifact", source.Artifacts),
                ("deployment", source.Deployments),
            }
        )
        foreach (var record in pair.Item2)
        {
            if (string.IsNullOrEmpty(Id(record)))
                Issue(
                    "unsupported_record_id",
                    Parent(record),
                    null,
                    "Record IDs must retain the application's stored string identity."
                );
            if (
                record.TryGetValue("contractId", out var parentValue)
                && !parentValue.IsBsonNull
                && !parentValue.IsString
            )
                Issue(
                    "invalid_parent_type",
                    Parent(record),
                    Id(record),
                    "An existing parent identity cannot be converted or replaced implicitly."
                );
            if (!projectIds.Contains(Parent(record)))
                Issue(
                    "missing_project",
                    Parent(record),
                    Id(record),
                    $"The {pair.Item1} references a missing project."
                );
        }
        foreach (var field in new[] { "transactionId", "activeAttemptKey" })
        {
            foreach (
                var record in source.Deployments.Where(item =>
                    item.TryGetValue(field, out var value) && (value.IsBsonNull || !value.IsString)
                )
            )
                Issue(
                    "invalid_unique_identity",
                    Parent(record),
                    Id(record),
                    $"The {field} must be absent or a nonempty string; reconcile invalid legacy identity fields explicitly."
                );
            var identified = source.Deployments.Where(item =>
                !string.IsNullOrEmpty(Text(item, field))
            );
            foreach (
                var group in identified
                    .GroupBy(item =>
                        field == "transactionId"
                            ? NormalizeHash(Text(item, field)!)
                            : Text(item, field)!
                    )
                    .Where(group => group.Count() > 1)
            )
            foreach (var record in group)
                Issue(
                    "duplicate_" + field,
                    Parent(record),
                    Id(record),
                    "A durable identity is reused by multiple records."
                );
        }
        foreach (var project in source.Projects)
        {
            var id = Id(project);
            if (string.IsNullOrEmpty(id))
            {
                Issue(
                    "unsupported_project_id",
                    null,
                    null,
                    "Project IDs must retain their original application string identity."
                );
                continue;
            }
            var artifacts = source.Artifacts.Where(item => Parent(item) == id).ToArray();
            var deployments = source.Deployments.Where(item => Parent(item) == id).ToArray();
            var children = source.Contracts.Where(item => Parent(item) == id).ToArray();
            var schema = project.GetValue("contractSchemaVersion", 0).IsInt32
                ? project.GetValue("contractSchemaVersion", 0).AsInt32
                : -1;
            if (schema is < 0 or > 1)
                Issue(
                    "unsupported_project_schema",
                    id,
                    id,
                    "Unknown project schema requires explicit review."
                );
            if (
                project.GetValue("isDeleting", false).ToBoolean()
                || Text(project, "contractWriteToken") is not null
            )
                Issue(
                    "project_writer_or_deletion",
                    id,
                    id,
                    "Drain the existing writer or reconcile deletion before migration."
                );
            foreach (
                var child in children.Where(item =>
                    Text(item, "testnetDeploymentWriteToken") is not null
                    || Text(item, "mainnetDeploymentWriteToken") is not null
                )
            )
                Issue(
                    "contract_writer_active",
                    id,
                    Id(child),
                    "An existing contract writer token must be reconciled before migration."
                );
            var typed = new Dictionary<string, DeploymentDocument>();
            foreach (var deployment in deployments)
            {
                try
                {
                    var attempt = BsonSerializer.Deserialize<DeploymentDocument>(deployment);
                    typed[attempt.Id] = attempt;
                    if (DeploymentRecoveryPolicy.IsUnresolved(attempt))
                        Issue(
                            attempt.BroadcastTrackingVersion == 1
                            && !attempt.BroadcastOutcomeUnknown
                            && string.IsNullOrEmpty(attempt.TransactionId)
                                ? "cancel_unbroadcast_via_lifecycle"
                                : "reconcile_broadcast_via_lifecycle",
                            id,
                            attempt.Id,
                            "Resolve this attempt using its original authenticated lifecycle before freezing migration mappings. No attempt is discarded by migration."
                        );
                    else if (attempt.ActiveAttemptKey is not null)
                        Issue(
                            "terminal_active_key",
                            id,
                            attempt.Id,
                            "Reconcile the terminal record's retained admission key; migration cannot discard it."
                        );
                    if (attempt.Network is not ("neo3:testnet" or "neo3:mainnet"))
                        Issue("invalid_network", id, attempt.Id, "Deployment network is unknown.");
                    if (
                        attempt.TransactionId is not null
                        && !Regex.IsMatch(attempt.TransactionId, "^(?:0[xX])?[0-9a-fA-F]{64}$")
                    )
                        Issue(
                            "invalid_transaction",
                            id,
                            attempt.Id,
                            "Original transaction identity requires reconciliation."
                        );
                    if (
                        DeploymentRecoveryPolicy.IsConfirmed(attempt)
                        && (
                            attempt.ContractHash is null
                            || !Regex.IsMatch(attempt.ContractHash, "^(?:0[xX])?[0-9a-fA-F]{40}$")
                        )
                    )
                        Issue(
                            "missing_confirmed_target",
                            id,
                            attempt.Id,
                            "A confirmed release has no valid original target."
                        );
                }
                catch (Exception error)
                    when (error
                            is FormatException
                                or BsonSerializationException
                                or InvalidOperationException
                    )
                {
                    Issue(
                        "invalid_deployment_document",
                        id,
                        Id(deployment),
                        "The original deployment format cannot be safely interpreted."
                    );
                }
            }
            inventory.Add(
                new(
                    id,
                    schema,
                    Text(project, "contractMigrationState"),
                    artifacts.Length,
                    deployments.Length,
                    children.Length,
                    typed
                        .Values.GroupBy(item => item.Network)
                        .Select(group => new NetworkInventory(
                            group.Key,
                            group
                                .Where(DeploymentRecoveryPolicy.IsConfirmed)
                                .Where(item => !string.IsNullOrEmpty(item.ContractHash))
                                .Select(item => NormalizeHash(item.ContractHash!))
                                .Distinct()
                                .Order()
                                .ToArray(),
                            group.Count(DeploymentRecoveryPolicy.IsUnresolved)
                        ))
                        .ToArray()
                )
            );
            if (
                artifacts
                    .Concat(deployments)
                    .Concat(children)
                    .Any(item => string.IsNullOrEmpty(Id(item)))
            )
                continue;
            var journal = source.Journals.SingleOrDefault(item => Id(item) == id);
            ProjectPlan? plan;
            if (journal is not null)
            {
                plan = JsonSerializer.Deserialize<ProjectPlan>(
                    journal["plan"].ToJson(),
                    MigrationRepository.JsonOptions
                );
                if (plan is null || journal.GetValue("version", 0) != 1)
                {
                    Issue(
                        "invalid_journal",
                        id,
                        null,
                        "An established migration journal is invalid."
                    );
                    continue;
                }
                foreach (
                    var supplied in decisions
                        .Projects.Where(item => item.ProjectId == id)
                        .SelectMany(item => item.Contracts)
                )
                {
                    var target = supplied.ExistingContractId ?? ContractId(id, supplied.Key);
                    if (
                        !plan.Contracts.Any(item =>
                            item.Id == target && item.Name == supplied.Name.Trim()
                        )
                        || supplied.ArtifactIds.Any(record =>
                            !plan.Records.Any(item =>
                                item.Kind == "artifact"
                                && item.Id == record
                                && item.ContractId == target
                            )
                        )
                        || supplied.DeploymentIds.Any(record =>
                            !plan.Records.Any(item =>
                                item.Kind == "deployment"
                                && item.Id == record
                                && item.ContractId == target
                            )
                        )
                    )
                        Issue(
                            "mapping_already_frozen",
                            id,
                            null,
                            "An established record/contract mapping cannot be changed on resume."
                        );
                }
                ValidateFrozen(plan, project, artifacts, deployments, issues);
            }
            else
                plan = schema is 0 or 1
                    ? PlanProject(
                        project,
                        children,
                        artifacts,
                        deployments,
                        decisions.Projects.FirstOrDefault(item => item.ProjectId == id),
                        issues
                    )
                    : null;
            if (plan is null)
                continue;
            ValidatePlan(plan, source, typed, issues);
            plans.Add(plan);
        }
        return new(1, inventory, plans, issues, source.Indexes);
    }

    private static ProjectPlan? PlanProject(
        BsonDocument project,
        BsonDocument[] children,
        BsonDocument[] artifacts,
        BsonDocument[] deployments,
        ProjectDecision? decision,
        List<MigrationIssue> issues
    )
    {
        var id = Id(project);
        var records = new List<RecordPlan>();
        var targets = new List<ContractPlan>();
        var assignments = new Dictionary<string, string>();
        var legacyArtifacts = artifacts.Where(item => Text(item, "contractId") is null).ToArray();
        var legacyDeployments = deployments
            .Where(item => Text(item, "contractId") is null)
            .ToArray();
        if (
            decision is null
            && children.Length > 0
            && (legacyArtifacts.Length > 0 || legacyDeployments.Length > 0)
        )
        {
            issues.Add(
                new(
                    "mixed_unmapped_history",
                    id,
                    null,
                    "Existing child contracts and unmapped legacy history require an explicit mapping."
                )
            );
            return null;
        }
        var groups =
            decision?.Contracts
            ?? (
                legacyArtifacts.Length + legacyDeployments.Length > 0
                    ? new[]
                    {
                        new ContractDecision
                        {
                            Key = "legacy",
                            Name = (Text(project, "name") ?? id).Trim(),
                            ArtifactIds = legacyArtifacts.Select(Id).ToArray(),
                            DeploymentIds = legacyDeployments
                                .Where(item => string.IsNullOrEmpty(Text(item, "artifactId")))
                                .Select(Id)
                                .ToArray(),
                        },
                    }
                    : []
            );
        foreach (var group in groups)
        {
            var name = group.Name.Trim();
            if (
                string.IsNullOrWhiteSpace(group.Key)
                || string.IsNullOrWhiteSpace(name)
                || name.Length > 128
            )
            {
                issues.Add(
                    new(
                        "invalid_contract_mapping",
                        id,
                        null,
                        "Each mapping needs a stable key and a name of 1-128 characters."
                    )
                );
                continue;
            }
            var targetId = group.ExistingContractId ?? ContractId(id, group.Key);
            var existing = children.SingleOrDefault(item => Id(item) == targetId);
            if (group.ExistingContractId is not null && existing is null)
                issues.Add(
                    new(
                        "invalid_mapping_parent",
                        id,
                        targetId,
                        "The selected existing contract does not belong to this project."
                    )
                );
            if (existing is not null && Text(existing, "name") != name)
                issues.Add(
                    new(
                        "mapping_name_conflict",
                        id,
                        targetId,
                        "The existing contract name must not be changed by migration."
                    )
                );
            var created =
                existing?.GetValue("createdAt", DateTime.UnixEpoch)
                ?? project.GetValue("createdAt", DateTime.UnixEpoch);
            targets.Add(
                new(
                    targetId,
                    id,
                    name,
                    created.ToUniversalTime(),
                    group.ExistingContractId is not null
                )
            );
            foreach (var recordId in group.ArtifactIds)
                Assign("artifact:" + recordId, targetId, assignments, id, issues);
            foreach (var recordId in group.DeploymentIds)
                Assign("deployment:" + recordId, targetId, assignments, id, issues);
        }
        foreach (
            var child in children.Where(child => targets.All(target => target.Id != Id(child)))
        )
            targets.Add(
                new(
                    Id(child),
                    id,
                    Text(child, "name") ?? "",
                    child.GetValue("createdAt", DateTime.UnixEpoch).ToUniversalTime(),
                    true
                )
            );
        var artifactTargets = new Dictionary<string, string>();
        foreach (var artifact in artifacts)
        {
            var original = Text(artifact, "contractId");
            assignments.TryGetValue("artifact:" + Id(artifact), out var mapped);
            var target = original ?? mapped;
            if (target is null)
            {
                issues.Add(
                    new(
                        "artifact_mapping_required",
                        id,
                        Id(artifact),
                        "Every legacy artifact needs one explicit mapping."
                    )
                );
                continue;
            }
            if (mapped is not null && original is not null && mapped != original)
                issues.Add(
                    new(
                        "immutable_parent",
                        id,
                        Id(artifact),
                        "An existing artifact parent cannot be changed."
                    )
                );
            artifactTargets[Id(artifact)] = target;
            records.Add(new("artifact", Id(artifact), id, target, original, Hash(artifact), false));
        }
        foreach (var deployment in deployments)
        {
            var original = Text(deployment, "contractId");
            var artifactId = Text(deployment, "artifactId");
            var derived =
                !string.IsNullOrEmpty(artifactId)
                && artifactTargets.TryGetValue(artifactId, out var artifactTarget)
                    ? artifactTarget
                    : null;
            assignments.TryGetValue("deployment:" + Id(deployment), out var mapped);
            if (!string.IsNullOrEmpty(artifactId) && derived is null)
                issues.Add(
                    new(
                        "missing_artifact",
                        id,
                        Id(deployment),
                        "Deployment artifact is missing or belongs to another project."
                    )
                );
            if (
                new[] { original, derived, mapped }
                    .Where(value => value is not null)
                    .Distinct()
                    .Count() > 1
            )
                issues.Add(
                    new(
                        "deployment_mapping_conflict",
                        id,
                        Id(deployment),
                        "Artifact, deployment, and explicit mappings disagree."
                    )
                );
            var target = original ?? derived ?? mapped;
            if (target is null)
            {
                issues.Add(
                    new(
                        "recovered_mapping_required",
                        id,
                        Id(deployment),
                        "Artifact-less deployment needs an explicit parent mapping."
                    )
                );
                continue;
            }
            var schema = deployment.GetValue("authorizationSnapshot", BsonNull.Value)
                is BsonDocument snapshot
                ? snapshot.GetValue("authorizationSchemaVersion", 1).ToInt32()
                : 1;
            records.Add(
                new(
                    "deployment",
                    Id(deployment),
                    id,
                    target,
                    original,
                    Hash(deployment),
                    original is null && schema <= 3
                )
            );
        }
        foreach (var key in assignments.Keys)
            if (!records.Any(record => record.Kind + ":" + record.Id == key))
                issues.Add(
                    new(
                        "unknown_record_mapping",
                        id,
                        key,
                        "A mapping names a missing or foreign record."
                    )
                );
        return new(
            id,
            Hash(project),
            project.GetValue("contractSchemaVersion", 0).ToInt32(),
            targets.OrderBy(item => item.Id).ToArray(),
            records.OrderBy(item => item.Kind).ThenBy(item => item.Id).ToArray()
        );
    }

    private static void Assign(
        string key,
        string target,
        Dictionary<string, string> assignments,
        string projectId,
        List<MigrationIssue> issues
    )
    {
        if (!assignments.TryAdd(key, target))
            issues.Add(
                new("duplicate_record_mapping", projectId, key, "A record may be mapped only once.")
            );
    }

    private static void ValidateFrozen(
        ProjectPlan plan,
        BsonDocument project,
        BsonDocument[] artifacts,
        BsonDocument[] deployments,
        List<MigrationIssue> issues
    )
    {
        if (plan.OriginalSchemaVersion == 1 && project.GetValue("contractSchemaVersion", 0) != 1)
            issues.Add(
                new(
                    "project_schema_changed",
                    plan.ProjectId,
                    plan.ProjectId,
                    "A converted project cannot be downgraded during resume."
                )
            );
        if (Hash(project) != plan.SourceHash)
            issues.Add(
                new(
                    "project_changed",
                    plan.ProjectId,
                    plan.ProjectId,
                    "The project changed after its mapping was frozen."
                )
            );
        var current = artifacts
            .Select(item => (Kind: "artifact", Document: item))
            .Concat(deployments.Select(item => (Kind: "deployment", Document: item)))
            .ToDictionary(item => item.Kind + ":" + Id(item.Document));
        if (
            current.Count != plan.Records.Count
            || plan.Records.Any(item => !current.ContainsKey(item.Kind + ":" + item.Id))
        )
            issues.Add(
                new(
                    "record_set_changed",
                    plan.ProjectId,
                    null,
                    "Records were added or removed after planning."
                )
            );
        foreach (var record in plan.Records)
        {
            if (!current.TryGetValue(record.Kind + ":" + record.Id, out var value))
                continue;
            var parent = Text(value.Document, "contractId");
            if (
                Hash(value.Document) != record.SourceHash
                || (parent != record.ContractId && parent != record.OriginalContractId)
            )
                issues.Add(
                    new(
                        "record_changed",
                        plan.ProjectId,
                        record.Id,
                        "A record changed or was relabeled after planning."
                    )
                );
        }
    }

    private static void ValidatePlan(
        ProjectPlan plan,
        MigrationSnapshot source,
        Dictionary<string, DeploymentDocument> typed,
        List<MigrationIssue> issues
    )
    {
        void Issue(string code, string? record, string message) =>
            issues.Add(new(code, plan.ProjectId, record, message));
        foreach (
            var duplicate in plan
                .Contracts.GroupBy(item => item.Id)
                .Where(group => group.Count() > 1)
        )
            Issue("duplicate_contract_mapping", duplicate.Key, "Contract keys must be unique.");
        foreach (
            var duplicate in plan
                .Contracts.GroupBy(item => item.Name.ToUpperInvariant())
                .Where(group => group.Count() > 1)
        )
            Issue(
                "duplicate_contract_name",
                null,
                "Mapped contract names must be unique within the project."
            );
        foreach (var contract in plan.Contracts)
        {
            var current = source.Contracts.SingleOrDefault(item => Id(item) == contract.Id);
            if (
                current is not null
                && Text(current, "normalizedName") != contract.Name.ToUpperInvariant()
            )
                Issue(
                    "invalid_normalized_contract_name",
                    contract.Id,
                    "Existing contract-name normalization requires explicit reconciliation."
                );
            if (
                current is not null
                && (Parent(current) != plan.ProjectId || Text(current, "name") != contract.Name)
            )
                Issue(
                    "contract_identity_conflict",
                    contract.Id,
                    "The stable planned contract identity is already used elsewhere."
                );
        }
        foreach (
            var record in plan.Records.Where(item =>
                plan.Contracts.All(contract => contract.Id != item.ContractId)
            )
        )
            Issue(
                "missing_contract",
                record.Id,
                "A mapped parent is missing or belongs to another project."
            );
        var mappedArtifacts = plan
            .Records.Where(item => item.Kind == "artifact")
            .ToDictionary(item => item.Id, item => (string?)item.ContractId);
        foreach (
            var duplicate in plan
                .Records.Where(item =>
                    item.Kind == "artifact"
                    && source.Artifacts.Any(artifact => Id(artifact) == item.Id)
                )
                .GroupBy(item =>
                    item.ContractId
                    + ":"
                    + Text(source.Artifacts.Single(artifact => Id(artifact) == item.Id), "version")
                )
                .Where(group => group.Count() > 1)
        )
            Issue(
                "duplicate_contract_version",
                null,
                "Mapped artifact versions would violate uniqueness."
            );
        var mappedDeployments = new List<DeploymentDocument>();
        foreach (var record in plan.Records.Where(item => item.Kind == "deployment"))
        {
            if (!typed.TryGetValue(record.Id, out var attempt))
                continue;
            var mapped = attempt with
            {
                ContractId = record.ContractId,
                LegacyContractMappingVerified =
                    record.AuditLegacy || attempt.LegacyContractMappingVerified,
            };
            mappedDeployments.Add(mapped);
            var expectedTarget = mapped.AuthorizationSnapshot?.ExpectedTargetContractHash;
            if (
                mapped.Operation == "update"
                && DeploymentRecoveryPolicy.IsConfirmed(mapped)
                && !string.IsNullOrEmpty(expectedTarget)
                && NormalizeHash(expectedTarget) != NormalizeHash(mapped.ContractHash ?? "")
            )
                Issue(
                    "original_update_target_mismatch",
                    record.Id,
                    "The stored update target disagrees with the confirmed address. Reconcile original evidence before mapping."
                );
            if (
                !DeploymentRecoveryPolicy.HasMappedHistory(
                    mapped,
                    plan.ProjectId,
                    plan.Contracts.Select(item => item.Id).ToHashSet(),
                    mappedArtifacts
                )
            )
                Issue(
                    "unsupported_original_evidence",
                    record.Id,
                    "The preserved original snapshot/history cannot authorize this mapping. Reconcile it explicitly before migration."
                );
        }
        foreach (
            var group in mappedDeployments
                .Where(DeploymentRecoveryPolicy.IsConfirmed)
                .GroupBy(item => (item.ContractId, item.Network))
        )
            if (
                group
                    .Where(item => !string.IsNullOrEmpty(item.ContractHash))
                    .Select(item => NormalizeHash(item.ContractHash!))
                    .Distinct()
                    .Count() > 1
            )
                Issue(
                    "ambiguous_lineage",
                    null,
                    "One mapped contract/network contains unrelated confirmed addresses. Supply explicit record mappings to separate contract entries."
                );
    }
}
