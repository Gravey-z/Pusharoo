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

    private ActionResult ForbidWithError(string error)
    {
        return StatusCode(StatusCodes.Status403Forbidden, new { error });
    }
}
