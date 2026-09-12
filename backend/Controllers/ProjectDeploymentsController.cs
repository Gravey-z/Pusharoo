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
    DeploymentCapabilityService deploymentCapabilities,
    ProjectAuthorizationService projectAuthorization,
    SignatureNonceService nonceService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DeploymentResponse>> CreateAsync(
        string projectId,
        CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        return Conflict(new { error = "Direct deployment recording is disabled. Start an authorized deployment attempt instead." });
    }

    [HttpPost("authorization-challenge")]
    public async Task<ActionResult<DeploymentAuthorizationChallengeResponse>> CreateAuthorizationChallengeAsync(
        string projectId,
        DeploymentAuthorizationChallengeRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasValidAuthorizationRequest(request.ArtifactId, request.Network, request.DeployedBy, request.Notes)
            || string.IsNullOrWhiteSpace(request.Origin) || string.IsNullOrWhiteSpace(request.Audience)
            || string.IsNullOrWhiteSpace(request.IssuedAtUtc) || string.IsNullOrWhiteSpace(request.Nonce))
        {
            return BadRequest(new { error = "Deployment authorization challenge fields are invalid." });
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }

        var permission = projectAuthorization.CanDeployToNetwork(projectResult.Value, request.DeployedBy, request.Network);
        if (!permission.IsAllowed) return StatusCode(permission.StatusCode, new { error = permission.Error });
        var capability = deploymentCapabilities.CanStartDeployment(permission.IsOwner);
        if (!capability.IsAvailable) return StatusCode(StatusCodes.Status501NotImplemented, new { error = capability.Reason });
        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var context = deploymentAuthorization.CreateContext(projectResult.Value, artifact, deployments, request.Network, request.DeployedBy, request.Notes);
        var message = deploymentAuthorization.BuildStartMessage(context, request).Replace(
            "{resolved-on-authorization}", permission.GrantRevision?.ToString() ?? "owner", StringComparison.Ordinal);
        return Ok(new DeploymentAuthorizationChallengeResponse(message, context.Operation, context.ExpectedTargetContractHash, context.ExpectedDeploymentRevision));
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
    public async Task<ActionResult<DeploymentResponse>> StartAttemptAsync(
        string projectId,
        StartDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasValidAuthorizationRequest(request.ArtifactId, request.Network, request.DeployedBy, request.Notes))
        {
            return BadRequest(new { error = "Artifact, network, and wallet address are required to start a deployment." });
        }

        var projectResult = await deploymentWorkflow.LoadProjectAsync(projectId, cancellationToken);
        if (!projectResult.IsSuccess || projectResult.Value is null) return WorkflowFailure(projectResult);
        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }
        var existingDeployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var authorization = deploymentAuthorization.ValidateStart(projectResult.Value, artifact, existingDeployments, request);
        if (!authorization.IsValid)
        {
            return StatusCode(authorization.StatusCode, new { error = authorization.Error });
        }
        var capability = deploymentCapabilities.CanStartDeployment(authorization.IsOwner);
        if (!capability.IsAvailable)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = capability.Reason });
        }
        if (!await nonceService.TryConsumeAsync(request.Authorization!, cancellationToken))
        {
            return Conflict(new { error = "This deployment authorization signature has already been used." });
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
    public async Task<ActionResult<DeploymentResponse>> MarkSubmittedAsync(
        string projectId,
        string deploymentId,
        SubmitDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TransactionId))
        {
            return BadRequest(new { error = "Transaction ID is required." });
        }

        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability, requireCurrentAccess: true, cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var attempt = context.Value.Attempt;
        var snapshotError = await ValidateAttemptSnapshotAsync(context.Value.Project, attempt, cancellationToken);
        if (snapshotError is not null)
        {
            return Conflict(new { error = snapshotError });
        }

        if (attempt.Status == "submitted" || attempt.Status == "confirmed")
        {
            return Ok(attempt.ToResponse());
        }

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
    public async Task<ActionResult<DeploymentResponse>> ConfirmAttemptAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability, requireCurrentAccess: false, cancellationToken);
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
            new RecoverDeploymentRequest(attempt.ArtifactId, attempt.Network, attempt.TransactionId!, attempt.DeployedBy, attempt.Notes, null),
            attempt.AuthorizationSnapshot?.InitiatorScriptHash,
            cancellationToken);
        if (!verification.IsValid || string.IsNullOrWhiteSpace(verification.ContractHash))
        {
            var isFinalFailure = verification.Error.StartsWith("Deployment transaction finished", StringComparison.Ordinal)
                || verification.Error.Contains("was not signed", StringComparison.Ordinal)
                || verification.Error.Contains("does not contain the expected", StringComparison.Ordinal)
                || verification.Error.Contains("did not include a contract hash", StringComparison.Ordinal);
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
    public async Task<ActionResult<DeploymentResponse>> MarkFailedAsync(
        string projectId,
        string deploymentId,
        FailDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var context = await LoadCapabilityAttemptAsync(projectId, deploymentId, request.AttemptCapability, requireCurrentAccess: true, cancellationToken);
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

    [HttpPost("recover")]
    public async Task<ActionResult<DeploymentResponse>> RecoverAsync(
        string projectId,
        RecoverDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var capability = deploymentCapabilities.CanRecoverUnboundTransaction();
        return StatusCode(StatusCodes.Status501NotImplemented, new { error = capability.Reason });
    }

    private async Task<DeploymentWorkflowResult<ProjectDeploymentAttemptContext>> LoadCapabilityAttemptAsync(
        string projectId,
        string deploymentId,
        string? attemptCapability,
        bool requireCurrentAccess,
        CancellationToken cancellationToken)
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
        if (!DeploymentAuthorizationService.IsValidCapability(attempt, attemptCapability))
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden("A valid, unexpired deployment attempt capability is required. Start a fresh signed attempt to continue.");
        }
        var snapshot = attempt.AuthorizationSnapshot;
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.InitiatorWalletAddress))
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden("Deployment attempt authorization is missing. Start a fresh signed attempt.");
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

    private async Task<string?> ValidateAttemptSnapshotAsync(
        ProjectDocument project,
        DeploymentDocument attempt,
        CancellationToken cancellationToken)
    {
        var snapshot = attempt.AuthorizationSnapshot;
        if (snapshot is null)
        {
            return "Deployment attempt authorization is missing. Start a fresh signed attempt.";
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
        if (!string.Equals(current.Operation, attempt.Operation, StringComparison.Ordinal)
            || current.ExpectedDeploymentRevision != snapshot.ExpectedDeploymentRevision
            || !string.Equals(current.ExpectedTargetContractHash, snapshot.ExpectedTargetContractHash, StringComparison.OrdinalIgnoreCase))
        {
            return "The deployment target or revision changed while this attempt was open. Review and sign a fresh attempt.";
        }

        return null;
    }

    private static bool HasValidAuthorizationRequest(string? artifactId, string? network, string? deployedBy, string? notes)
    {
        return !string.IsNullOrWhiteSpace(artifactId) && artifactId.Trim().Length <= 64
            && !string.IsNullOrWhiteSpace(network) && network.Trim().Length <= 64
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
