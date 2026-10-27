using backend.Models;
using static backend.Services.DeploymentAttemptValidator;

namespace backend.Services;

public static class DeploymentRecoveryPolicy
{
    public static bool IsConfirmed(DeploymentDocument attempt) =>
        attempt.Status == "confirmed" || string.IsNullOrWhiteSpace(attempt.Status);

    public static bool IsLegacyMapped(DeploymentDocument attempt) =>
        attempt.ContractId is not null
        && attempt.LegacyContractMappingVerified
        && (attempt.AuthorizationSnapshot?.AuthorizationSchemaVersion ?? 1) is >= 1 and <= 3;

    public static bool IsUnresolved(DeploymentDocument attempt) =>
        !IsConfirmed(attempt)
        && (
            attempt.ActiveAttemptKey is not null
            || attempt.BroadcastOutcomeUnknown
            || (
                attempt.Status == "failed"
                && !string.IsNullOrWhiteSpace(attempt.TransactionId)
                && !attempt.ChainFailureVerified
            )
            || attempt.Status
                is "preparing"
                    or "awaiting_wallet"
                    or "submitted"
                    or "confirming"
                    or "record_failed"
        );

    public static bool NeedsFreshLegacyReview(DeploymentDocument attempt) =>
        (attempt.AuthorizationSnapshot?.AuthorizationSchemaVersion ?? 1) < 4
        && attempt.BroadcastTrackingVersion == 0
        && string.IsNullOrWhiteSpace(attempt.TransactionId);

    public static string? ValidateRecoveryEvidence(DeploymentDocument attempt)
    {
        var evidence = attempt.RecoveryEvidence;
        if (
            evidence is null
            || evidence.Kind != "unrecorded-deploy"
            || attempt.AuthorizationSnapshot is not null
            || attempt.ArtifactId != string.Empty
            || attempt.ContractId is null
            || attempt.Operation != "deploy"
            || evidence.ProjectId != attempt.ProjectId
            || evidence.ContractId != attempt.ContractId
            || evidence.Network != attempt.Network
            || evidence.TransactionId != attempt.TransactionId
            || evidence.WalletAddress != attempt.DeployedBy
            || string.IsNullOrWhiteSpace(evidence.SignerScriptHash)
            || (
                IsConfirmed(attempt)
                && (
                    evidence.VerifiedAtUtc is null
                    || !HashesMatch(evidence.ContractHash, attempt.ContractHash)
                )
            )
        )
            return "The recovered deployment evidence does not match its stored parent or transaction.";

        return null;
    }

    public static string? ValidateLineage(
        IReadOnlyList<DeploymentDocument> history,
        DeploymentDocument attempt,
        string contractHash
    )
    {
        var unrelated = history.Any(item =>
            item.Id != attempt.Id
            && item.ProjectId == attempt.ProjectId
            && item.ContractId == attempt.ContractId
            && item.Network == attempt.Network
            && IsConfirmed(item)
            && !string.IsNullOrWhiteSpace(item.ContractHash)
            && !HashesMatch(item.ContractHash, contractHash)
        );
        return unrelated
            ? "This contract already has a different confirmed address on this network. Use a separate contract entry for a separate deployment lineage."
            : null;
    }

    public static bool HasMappedHistory(
        DeploymentDocument attempt,
        string projectId,
        IReadOnlySet<string> contractIds,
        IReadOnlyDictionary<string, string?> artifactParents
    )
    {
        if (
            attempt.ProjectId != projectId
            || attempt.ContractId is null
            || !contractIds.Contains(attempt.ContractId)
            || attempt.Network is not ("neo3:testnet" or "neo3:mainnet")
        )
            return false;

        var legacy = IsLegacyMapped(attempt);
        if (
            attempt.ActiveAttemptKey is not null
            && attempt.ActiveAttemptKey
                != DeploymentService.BuildActiveAttemptKey(
                    projectId,
                    attempt.Network,
                    attempt.ContractId
                )
            && !(
                legacy
                && attempt.ActiveAttemptKey
                    == DeploymentService.BuildActiveAttemptKey(projectId, attempt.Network)
            )
        )
            return false;

        if (attempt.RecoveryEvidence is not null)
            return ValidateRecoveryEvidence(attempt) is null;

        if (string.IsNullOrEmpty(attempt.ArtifactId))
            return legacy
                && IsConfirmed(attempt)
                && !string.IsNullOrWhiteSpace(attempt.TransactionId)
                && !string.IsNullOrWhiteSpace(attempt.ContractHash);

        if (
            !artifactParents.TryGetValue(attempt.ArtifactId, out var parent)
            || parent != attempt.ContractId
        )
            return false;

        var snapshot = attempt.AuthorizationSnapshot;
        if (legacy)
        {
            if (
                snapshot is { AuthorizationSchemaVersion: >= 3 }
                && (
                    snapshot.ProjectId != projectId
                    || (
                        snapshot.ContractId is not null && snapshot.ContractId != attempt.ContractId
                    )
                    || snapshot.ArtifactId != attempt.ArtifactId
                    || snapshot.Network != attempt.Network
                    || snapshot.Operation != attempt.Operation
                )
            )
                return false;
            return IsConfirmed(attempt) || snapshot is not null;
        }

        return snapshot is { AuthorizationSchemaVersion: 4 }
            && snapshot.ProjectId == projectId
            && snapshot.ContractId == attempt.ContractId
            && snapshot.ArtifactId == attempt.ArtifactId
            && snapshot.Network == attempt.Network
            && snapshot.Operation == attempt.Operation;
    }
}
