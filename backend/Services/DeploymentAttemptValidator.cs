using backend.Models;

namespace backend.Services;

public sealed class DeploymentAttemptValidator(
    DeploymentWorkflowService deploymentWorkflow,
    DeploymentService deploymentService,
    ArtifactService artifactService,
    DeploymentAuthorizationService deploymentAuthorization,
    DeploymentDataService deploymentData,
    ProjectAuthorizationService projectAuthorization,
    ProjectContractLoader contractLoader,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<DeploymentWorkflowResult<ProjectDeploymentAttemptContext>> LoadAsync(
        string projectId,
        string deploymentId,
        string? attemptCapability,
        WalletSessionIdentity actor,
        bool requireCurrentAccess,
        CancellationToken cancellationToken,
        bool allowExpiredFailedConfirmation = false,
        string? contractId = null
    )
    {
        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);

        if (!projectResult.IsSuccess || projectResult.Value is null)
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Failure(projectResult);
        }

        var attempt = await deploymentService.GetByIdAsync(deploymentId, cancellationToken);

        if (attempt is null || attempt.ProjectId != projectId || attempt.ContractId != contractId)
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.NotFound(
                "Deployment attempt was not found."
            );
        }

        var canRetryFailedConfirmation =
            allowExpiredFailedConfirmation
            && !string.IsNullOrWhiteSpace(attempt.TransactionId)
            && attempt.Status == "failed"
            && attempt.FailureStage == "confirmation"
            && attempt.FailureReason?.StartsWith(
                "Pusharoo could not verify the submitted invocation:",
                StringComparison.Ordinal
            ) == true
            && DeploymentAuthorizationService.MatchesCapabilityHash(attempt, attemptCapability);

        if (
            !DeploymentAuthorizationService.IsValidCapability(attempt, attemptCapability, clock)
            && !canRetryFailedConfirmation
        )
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden(
                "A valid, unexpired deployment attempt capability is required. Resume the attempt to continue."
            );
        }

        var snapshot = attempt.AuthorizationSnapshot;

        if (
            (snapshot is null || string.IsNullOrWhiteSpace(snapshot.InitiatorWalletAddress))
            && attempt.RecoveryEvidence is null
        )
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden(
                "Deployment attempt authorization is missing. Start a new attempt."
            );
        }

        if (!MatchesInitiator(attempt, actor))
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden(
                "Sign in with the wallet that started this deployment attempt."
            );
        }

        if (requireCurrentAccess)
        {
            var access = projectAuthorization.CanDeployToNetwork(
                projectResult.Value,
                snapshot?.InitiatorWalletAddress ?? attempt.RecoveryEvidence!.WalletAddress,
                attempt.Network
            );

            if (!access.IsAllowed)
            {
                return new DeploymentWorkflowResult<ProjectDeploymentAttemptContext>(
                    null,
                    access.StatusCode,
                    access.Error
                );
            }
        }

        if (contractId is not null)
            await contractLoader.LoadAsync(projectId, contractId, cancellationToken);
        var error = await ValidateSnapshotAsync(
            projectResult.Value,
            attempt,
            cancellationToken,
            requireCurrentTarget: attempt.Status == "awaiting_wallet"
                && !attempt.BroadcastOutcomeUnknown
                && !DeploymentRecoveryPolicy.IsLegacyMapped(attempt)
        );
        if (error is not null)
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Conflict(error);

        return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Success(
            new ProjectDeploymentAttemptContext(projectResult.Value, attempt)
        );
    }

    internal static bool MatchesInitiator(DeploymentDocument attempt, WalletSessionIdentity actor)
    {
        if (attempt.AuthorizationSnapshot is { } snapshot)
            return attempt.DeployedBy == actor.Address
                && snapshot.InitiatorWalletAddress == actor.Address
                && HashesMatch(snapshot.InitiatorScriptHash, actor.ScriptHash);
        return attempt.RecoveryEvidence is { } evidence
            && DeploymentRecoveryPolicy.ValidateRecoveryEvidence(attempt) is null
            && evidence.WalletAddress == actor.Address
            && HashesMatch(evidence.SignerScriptHash, actor.ScriptHash);
    }

    public async Task<string?> ValidateSnapshotAsync(
        ProjectDocument project,
        DeploymentDocument attempt,
        CancellationToken cancellationToken,
        bool requireCurrentTarget = true
    )
    {
        if (attempt.RecoveryEvidence is not null)
        {
            if (attempt.ProjectId != project.Id)
                return "The recovery project does not match its stored parent.";
            await contractLoader.LoadAsync(
                project.Id,
                attempt.ContractId ?? string.Empty,
                cancellationToken
            );
            return DeploymentRecoveryPolicy.ValidateRecoveryEvidence(attempt);
        }

        var snapshot = attempt.AuthorizationSnapshot;

        if (snapshot is null)
        {
            return "Deployment attempt authorization is missing. Start a new attempt.";
        }

        var authorizationSchemaVersion = snapshot.AuthorizationSchemaVersion ?? 1;

        if (authorizationSchemaVersion is < 1 or > 4)
        {
            return "The deployment attempt uses an unsupported authorization format.";
        }

        if (
            authorizationSchemaVersion >= 3
            && (
                snapshot.AuthorizationMethod != "wallet-session"
                || string.IsNullOrWhiteSpace(snapshot.SessionReference)
                || snapshot.ProjectId != project.Id
                || snapshot.ArtifactId != attempt.ArtifactId
                || snapshot.Network != attempt.Network
                || snapshot.Operation != attempt.Operation
            )
        )
        {
            return "The deployment session authorization snapshot is incomplete or inconsistent.";
        }

        if (
            attempt.ProjectId != project.Id
            || (
                attempt.ContractId is not null
                && authorizationSchemaVersion != 4
                && !DeploymentRecoveryPolicy.IsLegacyMapped(attempt)
            )
            || (
                authorizationSchemaVersion == 4
                && (
                    string.IsNullOrWhiteSpace(attempt.ContractId)
                    || snapshot.ContractId != attempt.ContractId
                    || attempt.DeployedBy != snapshot.InitiatorWalletAddress
                    || string.IsNullOrWhiteSpace(snapshot.NotesSha256)
                    || attempt.Operation is not ("deploy" or "update")
                    || (
                        attempt.Operation == "deploy"
                        && snapshot.ExpectedTargetContractHash is not null
                    )
                    || (
                        attempt.Operation == "update"
                        && string.IsNullOrWhiteSpace(snapshot.ExpectedTargetContractHash)
                    )
                )
            )
        )
            return "The deployment contract authorization parent is incomplete or inconsistent.";

        if (
            DeploymentRecoveryPolicy.IsLegacyMapped(attempt)
            && attempt.Operation == "update"
            && string.IsNullOrWhiteSpace(snapshot.ExpectedTargetContractHash)
        )
            return "The legacy update target requires explicit reconciliation from its original evidence.";

        if (attempt.ContractId is not null)
            await contractLoader.LoadAsync(project.Id, attempt.ContractId, cancellationToken);

        if (authorizationSchemaVersion >= 2)
        {
            if (
                snapshot.DeploymentData is null
                || string.IsNullOrWhiteSpace(snapshot.DeploymentDataSha256)
                || string.IsNullOrWhiteSpace(snapshot.DeploymentDataFormatVersion)
            )
            {
                return "The authorized deployment data snapshot is incomplete. Start a new attempt.";
            }

            try
            {
                var normalizedData = deploymentData.Normalize(
                    snapshot.DeploymentData,
                    snapshot.DeploymentDataFormatVersion
                );

                if (
                    !string.Equals(
                        normalizedData.Sha256,
                        snapshot.DeploymentDataSha256,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        normalizedData.FormatVersion,
                        snapshot.DeploymentDataFormatVersion,
                        StringComparison.Ordinal
                    )
                )
                {
                    return "The authorized deployment data snapshot failed its integrity check.";
                }
            }
            catch (DeploymentDataValidationException)
            {
                return "The authorized deployment data snapshot is invalid.";
            }
        }

        var artifact = await artifactService.GetByIdAsync(attempt.ArtifactId, cancellationToken);

        if (
            artifact is null
            || artifact.ProjectId != project.Id
            || artifact.ContractId != attempt.ContractId
        )
        {
            return "The authorized deployment artifact is no longer available.";
        }

        var deployments = await deploymentService.GetForScopeAsync(
            project.Id,
            attempt.ContractId,
            cancellationToken
        );
        var current = deploymentAuthorization.CreateContext(
            project,
            artifact,
            deployments.Where(item => item.Id != attempt.Id).ToArray(),
            attempt.Network,
            attempt.DeployedBy,
            attempt.Notes
        );
        var artifactReview = deploymentAuthorization.CreateSessionReview(current);

        if (
            !string.Equals(
                snapshot.ArtifactNefSha256,
                artifactReview.ArtifactNefSha256,
                StringComparison.Ordinal
            )
            || !string.Equals(
                snapshot.ArtifactManifestSha256,
                artifactReview.ArtifactManifestSha256,
                StringComparison.Ordinal
            )
        )
        {
            return "The deployment artifact changed after the attempt was authorized.";
        }

        if (authorizationSchemaVersion == 4 && snapshot.NotesSha256 != artifactReview.NotesSha256)
            return "The deployment notes changed after the attempt was authorized.";

        if (
            requireCurrentTarget
            && (
                !string.Equals(current.Operation, attempt.Operation, StringComparison.Ordinal)
                || current.ExpectedDeploymentRevision != snapshot.ExpectedDeploymentRevision
                || !string.Equals(
                    current.ExpectedTargetContractHash,
                    snapshot.ExpectedTargetContractHash,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            return "The deployment target or revision changed while this attempt was open. Review a new attempt.";
        }

        return null;
    }

    internal static bool HasValidAuthorizationRequest(
        string? artifactId,
        string? network,
        string? deployedBy,
        string? notes
    )
    {
        return !string.IsNullOrWhiteSpace(artifactId)
            && artifactId.Trim().Length <= 64
            && network is "neo3:testnet" or "neo3:mainnet"
            && !string.IsNullOrWhiteSpace(deployedBy)
            && deployedBy.Trim().Length <= 128
            && (notes?.Length ?? 0) <= 2_000;
    }

    internal static bool HashesMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.Ordinal);
    }

    private static string NormalizeHash(string value)
    {
        var normalized = value.Trim();

        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..].ToLowerInvariant()
            : normalized.ToLowerInvariant();
    }
}
