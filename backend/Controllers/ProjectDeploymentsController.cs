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
    NeoDeploymentVerificationService deploymentVerification) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DeploymentResponse>> CreateAsync(
        string projectId,
        CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ArtifactId))
        {
            return BadRequest(new { error = "Artifact is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Network))
        {
            return BadRequest(new { error = "Network is required." });
        }

        if (string.IsNullOrWhiteSpace(request.DeployedBy))
        {
            return BadRequest(new { error = "Wallet address is required." });
        }
        if (request.ArtifactId.Trim().Length > 64 || request.Network.Trim().Length > 64
            || request.DeployedBy.Trim().Length > 128 || request.ContractHash?.Length > 128
            || request.TransactionId?.Length > 128 || request.Notes?.Length > 2_000)
        {
            return BadRequest(new { error = "One or more deployment fields exceed their maximum length." });
        }

        if (!string.IsNullOrWhiteSpace(request.TransactionId))
        {
            var existing = await deploymentService.GetByTransactionIdAsync(request.TransactionId.Trim(), cancellationToken);
            if (existing is not null)
            {
                return existing.ProjectId == projectId
                    ? Ok(existing.ToResponse())
                    : Conflict(new { error = "The transaction ID is already recorded for another project." });
            }
        }

        var context = await deploymentWorkflow.LoadOwnedArtifactAsync(projectId, request.ArtifactId, request.DeployedBy, cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);

        var existingDeployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var verification = await deploymentVerification.VerifyAsync(
            context.Value.Project,
            existingDeployments,
            request,
            cancellationToken);
        if (!verification.IsValid)
        {
            return BadRequest(new { error = verification.Error });
        }

        try
        {
            var deployment = await deploymentService.CreateAsync(projectId, context.Value.Artifact, request, null, cancellationToken);
            return Created($"/api/projects/{projectId}/deployments/{deployment.Id}", deployment.ToResponse());
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(new { error = "The deployment transaction is already recorded." });
        }
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
        if (string.IsNullOrWhiteSpace(request.ArtifactId) || string.IsNullOrWhiteSpace(request.Network)
            || string.IsNullOrWhiteSpace(request.DeployedBy))
        {
            return BadRequest(new { error = "Artifact, network, and wallet address are required to start a deployment." });
        }

        var context = await deploymentWorkflow.LoadOwnedArtifactAsync(projectId, request.ArtifactId, request.DeployedBy, cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var operation = await deploymentWorkflow.DetermineOperationAsync(projectId, request.Network, cancellationToken);
        var attempt = await deploymentService.StartAttemptAsync(projectId, context.Value.Artifact, request, operation, cancellationToken);
        return Created($"/api/projects/{projectId}/deployments/{attempt.Id}", attempt.ToResponse());
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

        var context = await deploymentWorkflow.LoadOwnedAttemptAsync(projectId, deploymentId, request.DeployedBy, "Only the project owner who started this deployment can update it.", cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);
        var attempt = context.Value.Attempt;

        if (attempt.Status == "submitted" || attempt.Status == "confirmed")
        {
            return Ok(attempt.ToResponse());
        }

        var existing = await deploymentService.GetByTransactionIdAsync(request.TransactionId.Trim(), cancellationToken);
        if (existing is not null && existing.Id != attempt.Id)
        {
            return Conflict(new { error = "The transaction ID is already recorded for another deployment." });
        }

        var updated = await deploymentService.MarkSubmittedAsync(attempt, request.TransactionId, cancellationToken);
        return Ok(updated.ToResponse());
    }

    [HttpPost("{deploymentId}/confirm")]
    public async Task<ActionResult<DeploymentResponse>> ConfirmAttemptAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        CancellationToken cancellationToken)
    {
        var context = await deploymentWorkflow.LoadOwnedAttemptAsync(projectId, deploymentId, request.DeployedBy, "Only the project owner who started this deployment can confirm it.", cancellationToken);
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
            new RecoverDeploymentRequest(attempt.ArtifactId, attempt.Network, attempt.TransactionId!, attempt.DeployedBy, attempt.Notes),
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
        var context = await deploymentWorkflow.LoadOwnedAttemptAsync(projectId, deploymentId, request.DeployedBy, "Only the project owner who started this deployment can update it.", cancellationToken);
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
        if (string.IsNullOrWhiteSpace(request.ArtifactId)
            || string.IsNullOrWhiteSpace(request.Network)
            || string.IsNullOrWhiteSpace(request.TransactionId)
            || string.IsNullOrWhiteSpace(request.DeployedBy))
        {
            return BadRequest(new { error = "Artifact, network, transaction ID, and wallet address are required to recover a deployment." });
        }

        if (request.ArtifactId.Trim().Length > 64 || request.Network.Trim().Length > 64
            || request.TransactionId.Trim().Length > 128 || request.DeployedBy.Trim().Length > 128
            || request.Notes?.Length > 2_000)
        {
            return BadRequest(new { error = "One or more recovery fields exceed their maximum length." });
        }

        var existing = await deploymentService.GetByTransactionIdAsync(request.TransactionId.Trim(), cancellationToken);
        if (existing is not null)
        {
            return existing.ProjectId == projectId
                ? Ok(existing.ToResponse())
                : Conflict(new { error = "The transaction ID is already recorded for another project." });
        }

        var context = await deploymentWorkflow.LoadOwnedArtifactAsync(projectId, request.ArtifactId, request.DeployedBy, cancellationToken);
        if (!context.IsSuccess || context.Value is null) return WorkflowFailure(context);

        var existingDeployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var verification = await deploymentVerification.RecoverAsync(
            context.Value.Project,
            existingDeployments,
            request,
            cancellationToken);
        if (!verification.IsValid || string.IsNullOrWhiteSpace(verification.ContractHash))
        {
            return BadRequest(new { error = verification.Error });
        }

        var createRequest = new CreateDeploymentRequest(
            request.ArtifactId,
            request.Network,
            verification.ContractHash,
            request.TransactionId,
            request.DeployedBy,
            request.Notes);

        try
        {
            var deployment = await deploymentService.CreateAsync(projectId, context.Value.Artifact, createRequest, null, cancellationToken);
            return Created($"/api/projects/{projectId}/deployments/{deployment.Id}", deployment.ToResponse());
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(new { error = "The deployment transaction is already recorded." });
        }
    }

    private ActionResult WorkflowFailure<T>(DeploymentWorkflowResult<T> result)
        => StatusCode(result.StatusCode, new { error = result.Error });
}
