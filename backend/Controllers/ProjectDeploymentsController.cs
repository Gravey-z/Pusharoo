using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/deployments")]
public sealed class ProjectDeploymentsController(
    ProjectService projectService,
    ArtifactService artifactService,
    DeploymentService deploymentService,
    ProjectOwnershipService projectOwnershipService,
    NeoDeploymentVerificationService deploymentVerification) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DeploymentResponse>> CreateAsync(
        string projectId,
        CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

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

        var ownershipValidation = projectOwnershipService.ValidateCanManage(project, request.DeployedBy);
        if (!ownershipValidation.IsValid)
        {
            return ForbidWithError(ownershipValidation.Error);
        }

        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }

        var existingDeployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var verification = await deploymentVerification.VerifyAsync(
            project,
            existingDeployments,
            request,
            cancellationToken);
        if (!verification.IsValid)
        {
            return BadRequest(new { error = verification.Error });
        }

        try
        {
            var deployment = await deploymentService.CreateAsync(projectId, artifact, request, null, cancellationToken);
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
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

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
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        if (string.IsNullOrWhiteSpace(request.ArtifactId) || string.IsNullOrWhiteSpace(request.Network)
            || string.IsNullOrWhiteSpace(request.DeployedBy))
        {
            return BadRequest(new { error = "Artifact, network, and wallet address are required to start a deployment." });
        }

        var ownershipValidation = projectOwnershipService.ValidateCanManage(project, request.DeployedBy);
        if (!ownershipValidation.IsValid)
        {
            return ForbidWithError(ownershipValidation.Error);
        }

        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }

        var deployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var operation = deployments.Any(deployment =>
            string.Equals(deployment.Network, request.Network.Trim(), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(deployment.ContractHash)
            && (string.IsNullOrWhiteSpace(deployment.Status) || deployment.Status == "confirmed"))
            ? "update"
            : "deploy";
        var attempt = await deploymentService.StartAttemptAsync(projectId, artifact, request, operation, cancellationToken);
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

        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        var attempt = await deploymentService.GetByIdAsync(deploymentId, cancellationToken);
        if (project is null || attempt is null || attempt.ProjectId != projectId)
        {
            return NotFound(new { error = "Deployment attempt was not found." });
        }

        var ownershipValidation = projectOwnershipService.ValidateCanManage(project, request.DeployedBy);
        if (!ownershipValidation.IsValid || !string.Equals(attempt.DeployedBy, request.DeployedBy.Trim(), StringComparison.Ordinal))
        {
            return ForbidWithError("Only the project owner who started this deployment can update it.");
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
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        var attempt = await deploymentService.GetByIdAsync(deploymentId, cancellationToken);
        if (project is null || attempt is null || attempt.ProjectId != projectId)
        {
            return NotFound(new { error = "Deployment attempt was not found." });
        }

        var ownershipValidation = projectOwnershipService.ValidateCanManage(project, request.DeployedBy);
        if (!ownershipValidation.IsValid || !string.Equals(attempt.DeployedBy, request.DeployedBy.Trim(), StringComparison.Ordinal))
        {
            return ForbidWithError("Only the project owner who started this deployment can confirm it.");
        }

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
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        var attempt = await deploymentService.GetByIdAsync(deploymentId, cancellationToken);
        if (project is null || attempt is null || attempt.ProjectId != projectId)
        {
            return NotFound(new { error = "Deployment attempt was not found." });
        }

        var ownershipValidation = projectOwnershipService.ValidateCanManage(project, request.DeployedBy);
        if (!ownershipValidation.IsValid || !string.Equals(attempt.DeployedBy, request.DeployedBy.Trim(), StringComparison.Ordinal))
        {
            return ForbidWithError("Only the project owner who started this deployment can update it.");
        }

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
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

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

        var ownershipValidation = projectOwnershipService.ValidateCanManage(project, request.DeployedBy);
        if (!ownershipValidation.IsValid)
        {
            return ForbidWithError(ownershipValidation.Error);
        }

        var artifact = await artifactService.GetByIdAsync(request.ArtifactId, cancellationToken);
        if (artifact is null || artifact.ProjectId != projectId)
        {
            return BadRequest(new { error = "Artifact does not belong to this project." });
        }

        var existingDeployments = await deploymentService.GetByProjectIdAsync(projectId, cancellationToken);
        var verification = await deploymentVerification.RecoverAsync(
            project,
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
            var deployment = await deploymentService.CreateAsync(projectId, artifact, createRequest, null, cancellationToken);
            return Created($"/api/projects/{projectId}/deployments/{deployment.Id}", deployment.ToResponse());
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(new { error = "The deployment transaction is already recorded." });
        }
    }

    private ActionResult ForbidWithError(string error)
    {
        return StatusCode(StatusCodes.Status403Forbidden, new { error });
    }
}
