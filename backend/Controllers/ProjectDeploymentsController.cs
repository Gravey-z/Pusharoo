using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/deployments")]
public sealed class ProjectDeploymentsController(
    DeploymentService deploymentService,
    DeploymentWorkflowService deploymentWorkflow,
    NeoDeploymentVerificationService deploymentVerification,
    ArtifactService artifactService,
    DeploymentAuthorizationService deploymentAuthorization,
    DeploymentDataService deploymentData,
    ProjectAuthorizationService projectAuthorization,
    CurrentWalletSessionAccessor currentWallet) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DeploymentResponse>> CreateAsync(
        string projectId,
        CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        return Conflict(new { error = "Direct deployment recording is disabled. Start an authorized deployment attempt instead." });
    }

    [HttpPost("review")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentReviewResponse>> ReviewAsync(
        string projectId,
        DeploymentReviewRequest request,
        CancellationToken cancellationToken)
    {
        var actor = currentWallet.Current!;
        if (!HasValidAuthorizationRequest(request.ArtifactId, request.Network, actor.Address, request.Notes))
        {
            return BadRequest(new { error = "Deployment review fields are invalid." });
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }

        var permission = projectAuthorization.CanDeployToNetwork(projectResult.Value, actor.Address, request.Network);
        if (!permission.IsAllowed) return StatusCode(permission.StatusCode, new { error = permission.Error });
        NormalizedDeploymentData normalizedData;
        try
        {
            normalizedData = deploymentData.Normalize(request.DeploymentData);
        }
        catch (DeploymentDataValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var context = deploymentAuthorization.CreateContext(
            projectResult.Value,
            artifact,
            deployments,
            request.Network,
            actor.Address,
            request.Notes,
            normalizedData);
        if (context.Operation == "update" && request.DeploymentData is not null)
        {
            return BadRequest(new { error = "Deployment initialization data can only be supplied for a new contract deployment." });
        }
        return Ok(deploymentAuthorization.CreateSessionReview(context));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeploymentResponse>>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!project.IsSuccess) return WorkflowFailure(project);

        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var response = deployments.Select(deployment => deployment.ToResponse()).ToArray();

        return Ok(response);
    }

    [HttpPost("attempts")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> StartAttemptAsync(
        string projectId,
        StartSessionDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var actor = currentWallet.Current!;
        if (!HasValidAuthorizationRequest(request.ArtifactId, request.Network, actor.Address, request.Notes))
        {
            return BadRequest(new { error = "Artifact and network are required to start a deployment." });
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }
        var existingDeployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var authorization = deploymentAuthorization.ValidateSessionStart(
            projectResult.Value, artifact, existingDeployments, request, actor);
        if (!authorization.IsValid)
        {
            return StatusCode(authorization.StatusCode, new { error = authorization.Error });
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
                cancellationToken);
            return Created(
                $"/api/projects/{projectId}/deployments/{attempt.Id}",
                attempt.ToResponse() with { AttemptCapability = attemptCapability });
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(new { error = "Another deployment attempt is already active for this project network." });
        }
    }

    [HttpPost("{deploymentId}/submitted")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> MarkSubmittedAsync(
        string projectId,
        string deploymentId,
        SubmitDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TransactionId)
            || request.TransactionId.Length != 66
            || !request.TransactionId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !request.TransactionId[2..].All(Uri.IsHexDigit))
        {
            return BadRequest(new { error = "Enter a 0x-prefixed 64-character transaction hash." });
        }

        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability, requireCurrentAccess: false, cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var attempt = context.Value.Attempt;
        var snapshotError = await ValidateAttemptSnapshotAsync(context.Value.Project, attempt, cancellationToken);
        if (snapshotError is not null)
        {
            return Conflict(new { error = snapshotError });
        }

        if (attempt.Status == "submitted" || attempt.Status == "confirmed")
        {
            return string.Equals(attempt.TransactionId, request.TransactionId, StringComparison.OrdinalIgnoreCase)
                ? Ok(attempt.ToResponse())
                : Conflict(new { error = "This attempt already records a different transaction." });
        }
        if (attempt.Status != "awaiting_wallet")
            return Conflict(new { error = "This deployment attempt is no longer awaiting a wallet transaction." });

        var existing = await deploymentService.GetByTransactionIdAsync(request.TransactionId.Trim(), cancellationToken);
        if (existing is not null && existing.Id != attempt.Id)
        {
            return Conflict(new { error = "The transaction ID is already recorded for another deployment." });
        }

        var nextAttemptCapability = DeploymentAuthorizationService.CreateAttemptCapability();
        var updated = await deploymentService.MarkSubmittedAsync(attempt, request.TransactionId, nextAttemptCapability, cancellationToken);
        return Ok(updated.ToResponse() with { AttemptCapability = nextAttemptCapability });
    }

    [HttpPost("{deploymentId}/confirm")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> ConfirmAttemptAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability,
            requireCurrentAccess: false, cancellationToken, allowExpiredFailedConfirmation: true);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var project = context.Value.Project;
        var attempt = context.Value.Attempt;

        if (attempt.Status == "confirmed")
        {
            return Ok(attempt.ToResponse());
        }

        if (string.IsNullOrWhiteSpace(attempt.TransactionId))
        {
            return BadRequest(new { error = "The wallet has not submitted a transaction for this deployment." });
        }

        attempt = await deploymentService.MarkConfirmingAsync(attempt, cancellationToken);

        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var verification = await deploymentVerification.RecoverAsync(
            project,
            deployments.Where(deployment => deployment.Id != attempt.Id).ToArray(),
            attempt,
            cancellationToken);
        if (!verification.IsValid || string.IsNullOrWhiteSpace(verification.ContractHash))
        {
            var isFinalFailure = verification.Error.StartsWith("Deployment transaction finished", StringComparison.Ordinal)
                || verification.Error.Contains("was not signed", StringComparison.Ordinal)
                || verification.Error.Contains("does not contain the expected", StringComparison.Ordinal)
                || verification.Error.Contains("did not include a contract hash", StringComparison.Ordinal)
                || verification.Error.StartsWith("Pusharoo could not verify the submitted invocation", StringComparison.Ordinal);
            if (isFinalFailure)
            {
                var failed = await deploymentService.MarkFailedAsync(attempt, "confirmation", verification.Error, cancellationToken);
                return BadRequest(new { error = failed.FailureReason, deployment = failed.ToResponse() });
            }

            var submitted = attempt with { Status = "submitted", FailureStage = "confirmation", FailureReason = verification.Error, UpdatedAt = DateTime.UtcNow };
            await deploymentService.UpdateAsync(submitted, cancellationToken);
            return Conflict(new { error = verification.Error, deployment = submitted.ToResponse() });
        }

        if (string.Equals(attempt.Operation, "update", StringComparison.Ordinal)
            && !HashesMatch(verification.ContractHash, attempt.AuthorizationSnapshot?.ExpectedTargetContractHash))
        {
            var failed = await deploymentService.MarkFailedAsync(
                attempt,
                "confirmation",
                "Deployment update returned a contract hash other than the authorized target.",
                cancellationToken);
            return BadRequest(new { error = failed.FailureReason, deployment = failed.ToResponse() });
        }

        var confirmed = await deploymentService.MarkConfirmedAsync(attempt, verification.ContractHash, cancellationToken);
        return Ok(confirmed.ToResponse());
    }

    [HttpPost("{deploymentId}/failed")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> MarkFailedAsync(
        string projectId,
        string deploymentId,
        FailDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability, requireCurrentAccess: false, cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var attempt = context.Value.Attempt;

        if (attempt.Status == "confirmed")
        {
            return Conflict(new { error = "A confirmed deployment cannot be marked failed." });
        }

        var stage = request.Stage is "preparing" or "wallet" or "confirmation" or "record"
            ? request.Stage
            : "record";
        var failed = await deploymentService.MarkFailedAsync(attempt, stage, request.Reason, cancellationToken);
        return Ok(failed.ToResponse());
    }

    [HttpPost("{deploymentId}/resume")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> ResumeAttemptAsync(
        string projectId, string deploymentId, CancellationToken cancellationToken)
    {
        var actor = currentWallet.Current!;
        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var attempt = await deploymentService.GetByIdAsync(deploymentId, cancellationToken);
        if (attempt is null || attempt.ProjectId != projectId) return NotFound(new { error = "Deployment attempt was not found." });
        if (!MatchesInitiator(attempt, actor))
            return StatusCode(403, new { error = "Sign in with the wallet that started this deployment attempt." });
        var retryableFailure = attempt.Status == "failed" && attempt.FailureStage == "confirmation"
            && !string.IsNullOrWhiteSpace(attempt.TransactionId);
        if (attempt.Status is not ("awaiting_wallet" or "submitted" or "confirming") && !retryableFailure)
            return Conflict(new { error = "This deployment attempt cannot be resumed." });
        var capability = DeploymentAuthorizationService.CreateAttemptCapability();
        var renewed = await deploymentService.RenewCapabilityAsync(attempt, capability, cancellationToken);
        if (renewed is null) return Conflict(new { error = "The attempt changed. Refresh and try again." });
        return Ok(renewed.ToResponse() with { AttemptCapability = capability });
    }

    [HttpPost("recover")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> RecoverAsync(
        string projectId,
        RecoverDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Network is not ("neo3:testnet" or "neo3:mainnet")
            || string.IsNullOrWhiteSpace(request.TransactionId)
            || request.TransactionId.Length != 66
            || !request.TransactionId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !request.TransactionId[2..].All(Uri.IsHexDigit))
        {
            return BadRequest(new { error = "Select a Neo N3 network and enter a 0x-prefixed 64-character transaction hash." });
        }

        var actor = currentWallet.Current!;
        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var project = projectResult.Value;
        var transactionId = request.TransactionId.ToLowerInvariant();
        var existing = await deploymentService.GetByTransactionIdAsync(transactionId, cancellationToken);
        if (existing is not null && (existing.ProjectId != projectId
            || existing.Network != request.Network || existing.DeployedBy != actor.Address))
        {
            return Conflict(new { error = "This transaction is already recorded for a different deployment." });
        }
        if (existing?.Status == "confirmed") return Ok(existing.ToResponse());
        if (existing is not null && (existing.AuthorizationSnapshot is null
            || !MatchesInitiator(existing, actor)))
        {
            return Conflict(new { error = "The recorded attempt was authorized by a different wallet." });
        }
        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var active = existing is null ? deployments.FirstOrDefault(item => item.Network == request.Network
            && item.Status is "preparing" or "awaiting_wallet" or "submitted" or "confirming") : null;
        if (active is not null && (!MatchesInitiator(active, actor) || !string.IsNullOrWhiteSpace(active.TransactionId)))
        {
            return Conflict(new { error = "Another deployment attempt is active on this network. Resolve it before recovering a different transaction." });
        }
        if (existing is null && active is null)
        {
            var permission = projectAuthorization.CanDeployToNetwork(project, actor.Address, request.Network);
            if (!permission.IsAllowed) return StatusCode(permission.StatusCode, new { error = permission.Error });
        }
        var confirmedOnNetwork = deployments.Any(item => item.Network == request.Network
            && item.Status == "confirmed" && !string.IsNullOrWhiteSpace(item.ContractHash));
        if (existing is null && active is null && confirmedOnNetwork)
        {
            return Conflict(new { error = "This project already has a contract on that network. Recover its recorded attempt or update it from Pusharoo." });
        }
        var trackedAttempt = existing ?? active;
        var inspection = trackedAttempt is null
            ? await deploymentVerification.InspectRecoveredDeployAsync(project, request.Network, transactionId, actor.ScriptHash, cancellationToken)
            : await deploymentVerification.RecoverAsync(project,
                deployments.Where(item => item.Id != trackedAttempt.Id).ToArray(),
                trackedAttempt with { TransactionId = transactionId }, cancellationToken);
        if (!inspection.IsValid || string.IsNullOrWhiteSpace(inspection.ContractHash))
        {
            return Conflict(new { error = inspection.Error });
        }
        if (trackedAttempt?.Operation == "update"
            && !HashesMatch(inspection.ContractHash, trackedAttempt.AuthorizationSnapshot?.ExpectedTargetContractHash))
        {
            return Conflict(new { error = "The update transaction does not target the contract authorized by its original attempt." });
        }
        if (trackedAttempt is not null)
        {
            var confirmed = await deploymentService.MarkConfirmedAsync(
                trackedAttempt with { TransactionId = transactionId }, inspection.ContractHash, cancellationToken);
            return Ok(confirmed.ToResponse());
        }

        try
        {
            var recovered = await deploymentService.CreateRecoveredAsync(projectId, request.Network,
                transactionId, actor.Address, inspection.ContractHash, cancellationToken);
            return Created($"/api/projects/{projectId}/deployments/{recovered.Id}", recovered.ToResponse());
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(new { error = "This transaction is already recorded. Refresh the deployments list." });
        }
    }

    private async Task<DeploymentWorkflowResult<ProjectDeploymentAttemptContext>> LoadCapabilityAttemptAsync(
        string projectId,
        string deploymentId,
        string? attemptCapability,
        bool requireCurrentAccess,
        CancellationToken cancellationToken,
        bool allowExpiredFailedConfirmation = false)
    {
        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null)
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Failure(projectResult);
        }
        var attempt = await deploymentService.GetByIdAsync(deploymentId, cancellationToken);
        if (attempt is null || attempt.ProjectId != projectId)
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.NotFound("Deployment attempt was not found.");
        }
        var canRetryFailedConfirmation = allowExpiredFailedConfirmation
            && !string.IsNullOrWhiteSpace(attempt.TransactionId)
            && attempt.Status == "failed"
            && attempt.FailureStage == "confirmation"
            && attempt.FailureReason?.StartsWith("Pusharoo could not verify the submitted invocation:", StringComparison.Ordinal) == true
            && DeploymentAuthorizationService.MatchesCapabilityHash(attempt, attemptCapability);
        if (!DeploymentAuthorizationService.IsValidCapability(attempt, attemptCapability) && !canRetryFailedConfirmation)
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden("A valid, unexpired deployment attempt capability is required. Resume the attempt to continue.");
        }
        var snapshot = attempt.AuthorizationSnapshot;
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.InitiatorWalletAddress))
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden("Deployment attempt authorization is missing. Start a new attempt.");
        }
        if (!MatchesInitiator(attempt, currentWallet.Current!))
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden(
                "Sign in with the wallet that started this deployment attempt.");
        }
        if (requireCurrentAccess)
        {
            var access = projectAuthorization.CanDeployToNetwork(projectResult.Value, snapshot.InitiatorWalletAddress, attempt.Network);
            if (!access.IsAllowed)
            {
                return new DeploymentWorkflowResult<ProjectDeploymentAttemptContext>(null, access.StatusCode, access.Error);
            }
        }

        return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Success(
            new ProjectDeploymentAttemptContext(projectResult.Value, attempt));
    }

    private static bool MatchesInitiator(DeploymentDocument attempt, WalletSessionIdentity actor)
        => attempt.AuthorizationSnapshot is { } snapshot
            && string.Equals(snapshot.InitiatorWalletAddress, actor.Address, StringComparison.Ordinal)
            && HashesMatch(snapshot.InitiatorScriptHash, actor.ScriptHash);

    private async Task<string?> ValidateAttemptSnapshotAsync(
        ProjectDocument project,
        DeploymentDocument attempt,
        CancellationToken cancellationToken)
    {
        var snapshot = attempt.AuthorizationSnapshot;
        if (snapshot is null)
        {
            return "Deployment attempt authorization is missing. Start a new attempt.";
        }
        var authorizationSchemaVersion = snapshot.AuthorizationSchemaVersion ?? 1;
        if (authorizationSchemaVersion is < 1 or > 3)
        {
            return "The deployment attempt uses an unsupported authorization format.";
        }
        if (authorizationSchemaVersion == 3
            && (snapshot.AuthorizationMethod != "wallet-session"
                || string.IsNullOrWhiteSpace(snapshot.SessionReference)
                || snapshot.ProjectId != project.Id
                || snapshot.ArtifactId != attempt.ArtifactId
                || snapshot.Network != attempt.Network
                || snapshot.Operation != attempt.Operation))
        {
            return "The deployment session authorization snapshot is incomplete or inconsistent.";
        }
        if (authorizationSchemaVersion >= 2)
        {
            if (snapshot.DeploymentData is null || string.IsNullOrWhiteSpace(snapshot.DeploymentDataSha256)
                || string.IsNullOrWhiteSpace(snapshot.DeploymentDataFormatVersion))
            {
                return "The authorized deployment data snapshot is incomplete. Start a new attempt.";
            }
            try
            {
                var normalizedData = deploymentData.Normalize(snapshot.DeploymentData, snapshot.DeploymentDataFormatVersion);
                if (!string.Equals(normalizedData.Sha256, snapshot.DeploymentDataSha256, StringComparison.Ordinal)
                    || !string.Equals(normalizedData.FormatVersion, snapshot.DeploymentDataFormatVersion, StringComparison.Ordinal))
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
        if (artifact is null || artifact.ProjectId != project.Id)
        {
            return "The authorized deployment artifact is no longer available.";
        }
        var deployments = await deploymentService.GetByProjectIdAsync(project.Id, cancellationToken);
        var current = deploymentAuthorization.CreateContext(
            project,
            artifact,
            deployments.Where(item => item.Id != attempt.Id).ToArray(),
            attempt.Network,
            attempt.DeployedBy,
            attempt.Notes);
        var artifactReview = deploymentAuthorization.CreateSessionReview(current);
        if (!string.Equals(snapshot.ArtifactNefSha256, artifactReview.ArtifactNefSha256, StringComparison.Ordinal)
            || !string.Equals(snapshot.ArtifactManifestSha256, artifactReview.ArtifactManifestSha256, StringComparison.Ordinal))
        {
            return "The deployment artifact changed after the attempt was authorized.";
        }
        if (!string.Equals(current.Operation, attempt.Operation, StringComparison.Ordinal)
            || current.ExpectedDeploymentRevision != snapshot.ExpectedDeploymentRevision
            || !string.Equals(current.ExpectedTargetContractHash, snapshot.ExpectedTargetContractHash, StringComparison.OrdinalIgnoreCase))
        {
            return "The deployment target or revision changed while this attempt was open. Review a new attempt.";
        }

        return null;
    }

    private static bool HasValidAuthorizationRequest(string? artifactId, string? network, string? deployedBy, string? notes)
    {
        return !string.IsNullOrWhiteSpace(artifactId) && artifactId.Trim().Length <= 64
            && network is "neo3:testnet" or "neo3:mainnet"
            && !string.IsNullOrWhiteSpace(deployedBy) && deployedBy.Trim().Length <= 128
            && (notes?.Length ?? 0) <= 2_000;
    }

    private static bool HashesMatch(string? left, string? right)
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

    private ActionResult WorkflowFailure<T>(DeploymentWorkflowResult<T> result)
        => StatusCode(result.StatusCode, new { error = result.Error });
}
