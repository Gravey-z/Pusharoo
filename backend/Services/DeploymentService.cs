using backend.Models;
using backend.Repositories;
using MongoDB.Bson;

namespace backend.Services;

public sealed class DeploymentService(
    IDeploymentRepository deployments,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<DeploymentDocument> CreateAsync(
        string projectId,
        ArtifactDocument artifact,
        CreateDeploymentRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        if (artifact.ProjectId != projectId)
            throw new ContractOperationException(
                400,
                "deployment_parent_mismatch",
                "Artifact does not belong to this project."
            );

        var deployment = new DeploymentDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ProjectId = projectId,
            ContractId = artifact.ContractId,
            ArtifactId = artifact.Id,
            Version = artifact.Version,
            Network = request.Network.Trim(),
            ContractHash = TrimToNull(request.ContractHash),
            TransactionId = TrimToNull(request.TransactionId),
            DeployedBy = request.DeployedBy.Trim(),
            Notes = TrimToNull(request.Notes),
            Operation = "deploy",
            Status = "confirmed",
            IdempotencyKey = idempotencyKey,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
        };

        await deployments.InsertAsync(deployment, cancellationToken);

        return deployment;
    }

    public Task<DeploymentDocument?> GetByTransactionIdAsync(
        string transactionId,
        CancellationToken cancellationToken
    ) => deployments.GetByTransactionIdAsync(transactionId, cancellationToken);

    public async Task<DeploymentDocument> CreateRecoveredAsync(
        string projectId,
        string network,
        string transactionId,
        string deployedBy,
        string contractHash,
        CancellationToken cancellationToken
    )
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var deployment = new DeploymentDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ProjectId = projectId,
            ArtifactId = string.Empty,
            Version = "Recovered",
            Network = network,
            TransactionId = transactionId,
            DeployedBy = deployedBy,
            ContractHash = contractHash,
            Operation = "deploy",
            Status = "confirmed",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await deployments.InsertAsync(deployment, cancellationToken);

        return deployment;
    }

    public async Task<DeploymentDocument> CreateRecoveryPendingAsync(
        string projectId,
        string contractId,
        RecoverDeploymentRequest request,
        WalletSessionIdentity actor,
        string capability,
        CancellationToken cancellationToken
    )
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var attempt = new DeploymentDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ProjectId = projectId,
            ContractId = contractId,
            ArtifactId = string.Empty,
            Version = "Recovered",
            Network = request.Network,
            TransactionId = request.TransactionId.ToLowerInvariant(),
            DeployedBy = actor.Address,
            Status = "submitted",
            Operation = "deploy",
            BroadcastOutcomeUnknown = true,
            BroadcastTrackingVersion = 1,
            ActiveAttemptKey = BuildActiveAttemptKey(projectId, request.Network, contractId),
            AttemptCapabilityHash = DeploymentAuthorizationService.HashAttemptCapability(
                capability
            ),
            AttemptCapabilityExpiresAtUtc = now.AddMinutes(10),
            CreatedAt = now,
            UpdatedAt = now,
            RecoveryEvidence = new DeploymentRecoveryEvidence
            {
                ProjectId = projectId,
                ContractId = contractId,
                Network = request.Network,
                TransactionId = request.TransactionId.ToLowerInvariant(),
                WalletAddress = actor.Address,
                SignerScriptHash = actor.ScriptHash,
                RequestedAtUtc = now,
            },
        };
        await deployments.InsertAsync(attempt, cancellationToken);
        return attempt;
    }

    public async Task<DeploymentDocument> AttachRecoveryTransactionAsync(
        DeploymentDocument attempt,
        string transactionId,
        CancellationToken cancellationToken
    )
    {
        var updated = attempt with
        {
            TransactionId = attempt.TransactionId ?? transactionId.ToLowerInvariant(),
            Status = "submitted",
            BroadcastOutcomeUnknown = true,
            ActiveAttemptKey =
                attempt.ActiveAttemptKey
                ?? BuildActiveAttemptKey(attempt.ProjectId, attempt.Network, attempt.ContractId),
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
        };
        await deployments.ReplaceAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<DeploymentDocument> MarkWalletOpenedAsync(
        DeploymentDocument attempt,
        CancellationToken cancellationToken
    ) =>
        await deployments.MarkWalletOpenedAsync(
            attempt,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken
        )
        ?? throw new ContractOperationException(
            409,
            "wallet_already_opened",
            "This attempt changed or may already have opened a wallet transaction. Reconcile it before retrying."
        );

    public Task<DeploymentDocument?> GetByIdAsync(
        string deploymentId,
        CancellationToken cancellationToken
    ) => deployments.GetByIdAsync(deploymentId, cancellationToken);

    public async Task<IReadOnlyList<DeploymentDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        return await deployments.GetByProjectIdAsync(projectId, cancellationToken);
    }

    public Task<IReadOnlyList<DeploymentDocument>> GetForScopeAsync(
        string projectId,
        string? contractId,
        CancellationToken cancellationToken
    ) => deployments.GetForScopeAsync(projectId, contractId, cancellationToken);

    public async Task<DeploymentDocument> StartAttemptAsync(
        string projectId,
        ArtifactDocument artifact,
        StartSessionDeploymentAttemptRequest request,
        string operation,
        DeploymentAuthorizationSnapshot authorizationSnapshot,
        string attemptCapability,
        CancellationToken cancellationToken
    )
    {
        if (
            artifact.ProjectId != projectId
            || request.ArtifactId != artifact.Id
            || request.ContractId != artifact.ContractId
            || (
                artifact.ContractId is not null
                && (
                    authorizationSnapshot.AuthorizationSchemaVersion != 4
                    || authorizationSnapshot.ProjectId != projectId
                    || authorizationSnapshot.ContractId != artifact.ContractId
                    || authorizationSnapshot.ArtifactId != artifact.Id
                    || authorizationSnapshot.Network != request.Network.Trim()
                    || authorizationSnapshot.Operation != operation
                )
            )
        )
            throw new ContractOperationException(
                400,
                "deployment_parent_mismatch",
                "Deployment authorization does not match the selected artifact and contract."
            );

        var now = clock.GetUtcNow().UtcDateTime;
        var deployment = new DeploymentDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ProjectId = projectId,
            ContractId = artifact.ContractId,
            ArtifactId = artifact.Id,
            Version = artifact.Version,
            Network = request.Network.Trim(),
            DeployedBy = authorizationSnapshot.InitiatorWalletAddress,
            Notes = TrimToNull(request.Notes),
            Operation = operation,
            Status = "awaiting_wallet",
            BroadcastTrackingVersion = request.BroadcastTrackingVersion,
            ActiveAttemptKey = BuildActiveAttemptKey(
                projectId,
                request.Network,
                artifact.ContractId
            ),
            AuthorizationSnapshot = authorizationSnapshot,
            AttemptCapabilityHash = DeploymentAuthorizationService.HashAttemptCapability(
                attemptCapability
            ),
            AttemptCapabilityExpiresAtUtc = now.AddMinutes(10),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await deployments.InsertAsync(deployment, cancellationToken);

        return deployment;
    }

    public async Task<DeploymentDocument> MarkSubmittedAsync(
        DeploymentDocument deployment,
        string transactionId,
        string nextAttemptCapability,
        CancellationToken cancellationToken
    )
    {
        var updated = deployment with
        {
            TransactionId = transactionId.Trim().ToLowerInvariant(),
            Status = "submitted",
            BroadcastOutcomeUnknown = true,
            FailureStage = null,
            FailureReason = null,
            AttemptCapabilityHash = DeploymentAuthorizationService.HashAttemptCapability(
                nextAttemptCapability
            ),
            AttemptCapabilityExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(10),
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
        };
        await deployments.ReplaceAsync(updated, cancellationToken);

        return updated;
    }

    public Task<DeploymentDocument?> RenewCapabilityAsync(
        DeploymentDocument deployment,
        string nextAttemptCapability,
        CancellationToken cancellationToken
    ) =>
        deployments.RenewCapabilityAsync(
            deployment,
            DeploymentAuthorizationService.HashAttemptCapability(nextAttemptCapability),
            clock.GetUtcNow().UtcDateTime.AddMinutes(10),
            cancellationToken
        );

    public async Task<DeploymentDocument> MarkConfirmedAsync(
        DeploymentDocument deployment,
        string contractHash,
        CancellationToken cancellationToken
    )
    {
        var updated = deployment with
        {
            ContractHash = contractHash.Trim(),
            Status = "confirmed",
            BroadcastOutcomeUnknown = false,
            ChainFailureVerified = false,
            RecoveryEvidence = deployment.RecoveryEvidence is null
                ? null
                : deployment.RecoveryEvidence with
                {
                    ContractHash = contractHash.Trim(),
                    VerifiedAtUtc = clock.GetUtcNow().UtcDateTime,
                },
            FailureStage = null,
            FailureReason = null,
            ActiveAttemptKey = null,
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
        };
        await deployments.ReplaceAsync(updated, cancellationToken);

        return updated;
    }

    public async Task<DeploymentDocument> MarkConfirmingAsync(
        DeploymentDocument deployment,
        CancellationToken cancellationToken
    )
    {
        var updated = deployment with
        {
            Status = "confirming",
            ChainFailureVerified = false,
            ActiveAttemptKey =
                deployment.ActiveAttemptKey
                ?? BuildActiveAttemptKey(
                    deployment.ProjectId,
                    deployment.Network,
                    deployment.ContractId
                ),
            FailureStage = null,
            FailureReason = null,
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
        };
        await deployments.ReplaceAsync(updated, cancellationToken);

        return updated;
    }

    public async Task<DeploymentDocument> MarkFailedAsync(
        DeploymentDocument deployment,
        string stage,
        string reason,
        CancellationToken cancellationToken,
        bool verifiedFinalFailure = false
    )
    {
        var uncertain =
            !verifiedFinalFailure
            && (
                deployment.BroadcastOutcomeUnknown
                || !string.IsNullOrWhiteSpace(deployment.TransactionId)
                || DeploymentRecoveryPolicy.IsLegacyMapped(deployment)
                || (
                    deployment.BroadcastTrackingVersion == 0
                    && deployment.Status is "preparing" or "awaiting_wallet"
                )
            );
        var updated = deployment with
        {
            Status = uncertain || stage == "record" ? "record_failed" : "failed",
            BroadcastOutcomeUnknown = uncertain,
            ChainFailureVerified = verifiedFinalFailure,
            FailureStage = stage,
            FailureReason = TrimToNull(reason),
            ActiveAttemptKey = uncertain
                ? deployment.ActiveAttemptKey
                    ?? BuildActiveAttemptKey(
                        deployment.ProjectId,
                        deployment.Network,
                        deployment.ContractId
                    )
                : null,
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
        };
        await deployments.ReplaceAsync(updated, cancellationToken);

        return updated;
    }

    public Task UpdateAsync(DeploymentDocument deployment, CancellationToken cancellationToken) =>
        deployments.ReplaceAsync(deployment, cancellationToken);

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string BuildActiveAttemptKey(
        string projectId,
        string network,
        string? contractId = null
    ) =>
        contractId is null
            ? $"{projectId.Trim()}:{network.Trim()}"
            : $"{projectId.Trim()}:{contractId}:{network.Trim()}";
}
