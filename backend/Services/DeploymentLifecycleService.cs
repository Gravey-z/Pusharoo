using backend.Models;
using MongoDB.Driver;
using static backend.Services.DeploymentAttemptValidator;

namespace backend.Services;

public sealed class DeploymentLifecycleService(
    DeploymentService deploymentService,
    DeploymentWorkflowService deploymentWorkflow,
    NeoDeploymentVerificationService deploymentVerification,
    ArtifactService artifactService,
    DeploymentAuthorizationService deploymentAuthorization,
    DeploymentDataService deploymentData,
    ProjectAuthorizationService projectAuthorization,
    DeploymentAttemptValidator attemptValidator,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<DeploymentWorkflowResult<DeploymentReviewResponse>> ReviewAsync(
        string projectId,
        DeploymentReviewRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        if (
            !HasValidAuthorizationRequest(
                request.ArtifactId,
                request.Network,
                actor.Address,
                request.Notes
            )
        )
        {
            return DeploymentWorkflowResult<DeploymentReviewResponse>.BadRequest(
                "Deployment review fields are invalid."
            );
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);

        if (!projectResult.IsSuccess || projectResult.Value is null)
            return DeploymentWorkflowResult<DeploymentReviewResponse>.Failure(projectResult);

        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);

        if (
            artifact is null
            || artifact.ProjectId != projectId
            || artifact.ContractId != contractId
        )
        {
            return DeploymentWorkflowResult<DeploymentReviewResponse>.BadRequest(
                "Artifact does not belong to this project."
            );
        }

        var permission = projectAuthorization.CanDeployToNetwork(
            projectResult.Value,
            actor.Address,
            request.Network
        );

        if (!permission.IsAllowed)
            return DeploymentWorkflowResult<DeploymentReviewResponse>.Rejected(
                permission.StatusCode,
                permission.Error
            );

        NormalizedDeploymentData normalizedData;

        try
        {
            normalizedData = deploymentData.Normalize(request.DeploymentData);
        }
        catch (DeploymentDataValidationException exception)
        {
            return DeploymentWorkflowResult<DeploymentReviewResponse>.BadRequest(exception.Message);
        }

        var deployments = await deploymentService.GetForScopeAsync(
            projectId,
            contractId,
            cancellationToken
        );
        var context = deploymentAuthorization.CreateContext(
            projectResult.Value,
            artifact,
            deployments,
            request.Network,
            actor.Address,
            request.Notes,
            normalizedData
        );

        if (context.Operation == "update" && request.DeploymentData is not null)
        {
            return DeploymentWorkflowResult<DeploymentReviewResponse>.BadRequest(
                "Deployment initialization data can only be supplied for a new contract deployment."
            );
        }

        return DeploymentWorkflowResult<DeploymentReviewResponse>.Success(
            deploymentAuthorization.CreateSessionReview(context)
        );
    }

    public async Task<DeploymentWorkflowResult<IReadOnlyList<DeploymentResponse>>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        var project = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);

        if (!project.IsSuccess)
            return DeploymentWorkflowResult<IReadOnlyList<DeploymentResponse>>.Failure(project);

        var deployments = contractId is null
            ? await deploymentService.GetByProjectIdAsync(projectId, cancellationToken)
            : await deploymentService.GetForScopeAsync(projectId, contractId, cancellationToken);
        var response = deployments.Select(deployment => deployment.ToResponse()).ToArray();

        return DeploymentWorkflowResult<IReadOnlyList<DeploymentResponse>>.Success(response);
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> StartAttemptAsync(
        string projectId,
        StartSessionDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        if (
            !HasValidAuthorizationRequest(
                request.ArtifactId,
                request.Network,
                actor.Address,
                request.Notes
            )
        )
        {
            return DeploymentWorkflowResult<DeploymentResponse>.BadRequest(
                "Artifact and network are required to start a deployment."
            );
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);

        if (!projectResult.IsSuccess || projectResult.Value is null)
            return DeploymentWorkflowResult<DeploymentResponse>.Failure(projectResult);

        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);

        if (
            artifact is null
            || artifact.ProjectId != projectId
            || artifact.ContractId != contractId
        )
        {
            return DeploymentWorkflowResult<DeploymentResponse>.BadRequest(
                "Artifact does not belong to this project."
            );
        }

        var existingDeployments = await deploymentService.GetForScopeAsync(
            projectId,
            contractId,
            cancellationToken
        );
        if (
            existingDeployments.Any(item =>
                item.Network == request.Network && DeploymentRecoveryPolicy.IsUnresolved(item)
            )
        )
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "Another unresolved attempt exists for this contract and network. Reconcile it before starting a new release."
            );

        var authorization = deploymentAuthorization.ValidateSessionStart(
            projectResult.Value,
            artifact,
            existingDeployments,
            request,
            actor
        );

        if (!authorization.IsValid)
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Rejected(
                authorization.StatusCode,
                authorization.Error
            );
        }

        var attemptCapability = DeploymentAuthorizationService.CreateAttemptCapability();

        try
        {
            var attempt = await deploymentService.StartAttemptAsync(
                projectId,
                artifact,
                request,
                authorization.Context!.Operation,
                authorization.Snapshot!,
                attemptCapability,
                cancellationToken
            );

            return DeploymentWorkflowResult<DeploymentResponse>.Created(
                attempt.ToResponse() with
                {
                    AttemptCapability = attemptCapability,
                }
            );
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "Another deployment attempt is already active for this contract and network."
            );
        }
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> MarkWalletOpenedAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        var context = await attemptValidator.LoadAsync(
            projectId,
            deploymentId,
            request.AttemptCapability,
            actor,
            false,
            cancellationToken,
            contractId: contractId
        );
        if (!context.IsSuccess || context.Value is null)
            return DeploymentWorkflowResult<DeploymentResponse>.Failure(context);
        var attempt = context.Value.Attempt;
        if (
            attempt.Status != "awaiting_wallet"
            || attempt.BroadcastTrackingVersion != 1
            || attempt.BroadcastOutcomeUnknown
            || attempt.TransactionId is not null
        )
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This attempt may already have opened a wallet transaction. Reconcile it before opening another."
            );
        var updated = await deploymentService.MarkWalletOpenedAsync(attempt, cancellationToken);
        return DeploymentWorkflowResult<DeploymentResponse>.Success(updated.ToResponse());
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> MarkSubmittedAsync(
        string projectId,
        string deploymentId,
        SubmitDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        if (
            string.IsNullOrWhiteSpace(request.TransactionId)
            || request.TransactionId.Length != 66
            || !request.TransactionId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !request.TransactionId[2..].All(Uri.IsHexDigit)
        )
        {
            return DeploymentWorkflowResult<DeploymentResponse>.BadRequest(
                "Enter a 0x-prefixed 64-character transaction hash."
            );
        }

        var context = await attemptValidator.LoadAsync(
            projectId,
            deploymentId,
            request.AttemptCapability,
            actor,
            requireCurrentAccess: false,
            cancellationToken,
            contractId: contractId
        );

        if (!context.IsSuccess || context.Value is null)
            return DeploymentWorkflowResult<DeploymentResponse>.Failure(context);

        var attempt = context.Value.Attempt;
        var snapshotError = await attemptValidator.ValidateSnapshotAsync(
            context.Value.Project,
            attempt,
            cancellationToken,
            requireCurrentTarget: attempt.Status == "awaiting_wallet"
                && !attempt.BroadcastOutcomeUnknown
                && !DeploymentRecoveryPolicy.IsLegacyMapped(attempt)
        );

        if (snapshotError is not null)
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(snapshotError);
        }

        if (attempt.Status == "submitted" || DeploymentRecoveryPolicy.IsConfirmed(attempt))
        {
            return string.Equals(
                attempt.TransactionId,
                request.TransactionId,
                StringComparison.OrdinalIgnoreCase
            )
                ? DeploymentWorkflowResult<DeploymentResponse>.Success(attempt.ToResponse())
                : DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                    "This attempt already records a different transaction."
                );
        }

        if (attempt.Status != "awaiting_wallet")
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This deployment attempt is no longer awaiting a wallet transaction."
            );

        var existing = await deploymentService.GetByTransactionIdAsync(
            request.TransactionId.Trim(),
            cancellationToken
        );

        if (existing is not null && existing.Id != attempt.Id)
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "The transaction ID is already recorded for another deployment."
            );
        }

        var nextAttemptCapability = DeploymentAuthorizationService.CreateAttemptCapability();
        DeploymentDocument updated;
        try
        {
            updated = await deploymentService.MarkSubmittedAsync(
                attempt,
                request.TransactionId,
                nextAttemptCapability,
                cancellationToken
            );
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "The transaction ID is already recorded for another deployment."
            );
        }

        return DeploymentWorkflowResult<DeploymentResponse>.Success(
            updated.ToResponse() with
            {
                AttemptCapability = nextAttemptCapability,
            }
        );
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> ConfirmAttemptAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        var context = await attemptValidator.LoadAsync(
            projectId,
            deploymentId,
            request.AttemptCapability,
            actor,
            requireCurrentAccess: false,
            cancellationToken,
            allowExpiredFailedConfirmation: true,
            contractId: contractId
        );

        if (!context.IsSuccess || context.Value is null)
            return DeploymentWorkflowResult<DeploymentResponse>.Failure(context);

        var project = context.Value.Project;
        var attempt = context.Value.Attempt;

        if (attempt.Status == "failed" && attempt.ChainFailureVerified)
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This transaction has a verified failure. Start a freshly reviewed release instead."
            );

        if (DeploymentRecoveryPolicy.IsConfirmed(attempt))
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Success(attempt.ToResponse());
        }

        if (string.IsNullOrWhiteSpace(attempt.TransactionId))
        {
            return DeploymentWorkflowResult<DeploymentResponse>.BadRequest(
                "The wallet has not submitted a transaction for this deployment."
            );
        }

        var deployments = await deploymentService.GetForScopeAsync(
            projectId,
            contractId,
            cancellationToken
        );
        if (
            deployments.Any(item =>
                item.Id != attempt.Id
                && item.Network == attempt.Network
                && DeploymentRecoveryPolicy.IsUnresolved(item)
            )
        )
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "Another unresolved attempt exists for this contract and network. Reconcile it before retrying this transaction."
            );
        try
        {
            attempt = await deploymentService.MarkConfirmingAsync(attempt, cancellationToken);
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "Another attempt already owns this contract and network."
            );
        }
        var verification = await deploymentVerification.RecoverAsync(
            project,
            deployments.Where(deployment => deployment.Id != attempt.Id).ToArray(),
            attempt,
            cancellationToken
        );

        if (!verification.IsValid || string.IsNullOrWhiteSpace(verification.ContractHash))
        {
            var isFinalFailure =
                verification.Error.StartsWith(
                    "Deployment transaction finished with FAULT",
                    StringComparison.Ordinal
                ) || verification.Error.Contains("was not signed", StringComparison.Ordinal);

            if (isFinalFailure)
            {
                var failed = await deploymentService.MarkFailedAsync(
                    attempt,
                    "confirmation",
                    verification.Error,
                    cancellationToken,
                    verifiedFinalFailure: true
                );

                return DeploymentWorkflowResult<DeploymentResponse>.BadRequest(
                    failed.FailureReason,
                    failed.ToResponse()
                );
            }

            var submitted = attempt with
            {
                Status = "submitted",
                FailureStage = "confirmation",
                FailureReason = verification.Error,
                UpdatedAt = clock.GetUtcNow().UtcDateTime,
            };
            await deploymentService.UpdateAsync(submitted, cancellationToken);

            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                verification.Error,
                submitted.ToResponse()
            );
        }

        if (
            string.Equals(attempt.Operation, "update", StringComparison.Ordinal)
            && !HashesMatch(
                verification.ContractHash,
                attempt.AuthorizationSnapshot?.ExpectedTargetContractHash
            )
        )
        {
            var failed = await deploymentService.MarkFailedAsync(
                attempt,
                "confirmation",
                "Deployment update returned a contract hash other than the authorized target.",
                cancellationToken,
                verifiedFinalFailure: true
            );

            return DeploymentWorkflowResult<DeploymentResponse>.BadRequest(
                failed.FailureReason,
                failed.ToResponse()
            );
        }

        var lineageError = DeploymentRecoveryPolicy.ValidateLineage(
            deployments,
            attempt,
            verification.ContractHash
        );
        if (lineageError is not null)
        {
            var blocked = await deploymentService.MarkFailedAsync(
                attempt,
                "record",
                lineageError,
                cancellationToken
            );
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                lineageError,
                blocked.ToResponse()
            );
        }

        var confirmed = await deploymentService.MarkConfirmedAsync(
            attempt,
            verification.ContractHash,
            cancellationToken
        );

        return DeploymentWorkflowResult<DeploymentResponse>.Success(confirmed.ToResponse());
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> MarkFailedAsync(
        string projectId,
        string deploymentId,
        FailDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        var context = await attemptValidator.LoadAsync(
            projectId,
            deploymentId,
            request.AttemptCapability,
            actor,
            requireCurrentAccess: false,
            cancellationToken,
            contractId: contractId
        );

        if (!context.IsSuccess || context.Value is null)
            return DeploymentWorkflowResult<DeploymentResponse>.Failure(context);

        var attempt = context.Value.Attempt;

        if (attempt.Status == "failed" && attempt.ChainFailureVerified)
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This transaction has a verified failure. Start a freshly reviewed release instead."
            );

        if (DeploymentRecoveryPolicy.IsConfirmed(attempt))
        {
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "A confirmed deployment cannot be marked failed."
            );
        }

        var stage = request.Stage is "preparing" or "wallet" or "confirmation" or "record"
            ? request.Stage
            : "record";
        var failed = await deploymentService.MarkFailedAsync(
            attempt,
            stage,
            request.Reason,
            cancellationToken
        );

        return DeploymentWorkflowResult<DeploymentResponse>.Success(failed.ToResponse());
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> ResumeAttemptAsync(
        string projectId,
        string deploymentId,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);

        if (!projectResult.IsSuccess || projectResult.Value is null)
            return DeploymentWorkflowResult<DeploymentResponse>.Failure(projectResult);

        var attempt = await deploymentService.GetByIdAsync(deploymentId, cancellationToken);

        if (attempt is null || attempt.ProjectId != projectId || attempt.ContractId != contractId)
            return DeploymentWorkflowResult<DeploymentResponse>.NotFound(
                "Deployment attempt was not found."
            );

        if (!MatchesInitiator(attempt, actor))
            return DeploymentWorkflowResult<DeploymentResponse>.Rejected(
                403,
                "Sign in with the wallet that started this deployment attempt."
            );

        var snapshotError = await attemptValidator.ValidateSnapshotAsync(
            projectResult.Value,
            attempt,
            cancellationToken,
            requireCurrentTarget: attempt.Status == "awaiting_wallet"
                && !attempt.BroadcastOutcomeUnknown
                && !DeploymentRecoveryPolicy.IsLegacyMapped(attempt)
        );
        if (snapshotError is not null)
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(snapshotError);

        if (DeploymentRecoveryPolicy.NeedsFreshLegacyReview(attempt))
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This legacy attempt requires broadcast reconciliation and a fresh review before a new wallet transaction. Recover a known transaction using its original evidence."
            );

        var retryableFailure =
            attempt.Status == "failed"
            && !attempt.ChainFailureVerified
            && attempt.FailureStage == "confirmation"
            && !string.IsNullOrWhiteSpace(attempt.TransactionId);

        if (
            attempt.Status
                is not ("awaiting_wallet" or "submitted" or "confirming" or "record_failed")
            && !retryableFailure
        )
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "This deployment attempt cannot be resumed."
            );

        var capability = DeploymentAuthorizationService.CreateAttemptCapability();
        var renewed = await deploymentService.RenewCapabilityAsync(
            attempt,
            capability,
            cancellationToken
        );

        if (renewed is null)
            return DeploymentWorkflowResult<DeploymentResponse>.Conflict(
                "The attempt changed. Refresh and try again."
            );

        return DeploymentWorkflowResult<DeploymentResponse>.Success(
            renewed.ToResponse() with
            {
                AttemptCapability = capability,
            }
        );
    }
}
