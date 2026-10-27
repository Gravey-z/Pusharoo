using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/artifacts")]
public sealed class ProjectArtifactsController(
    ProjectService projectService,
    ArtifactService artifactService,
    CurrentWalletSessionAccessor walletSession,
    ProjectAuthorizationService authorization,
    ArtifactUploadReader uploadReader,
    ArtifactUploadService uploads
) : ControllerBase
{
    [HttpPost]
    [RequireWalletSession]
    [Consumes("multipart/form-data")]
    [DisableFormValueModelBinding]
    public async Task<ActionResult<ArtifactResponse>> UploadAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        var access = authorization.CanUploadArtifacts(project, walletSession.Current!.Address);

        if (!access.IsAllowed)
        {
            return StatusCode(access.StatusCode, new { error = access.Error });
        }

        var input = await uploadReader.ReadAsync(
            Request,
            project.Id,
            null,
            walletSession.Current!.Address,
            cancellationToken
        );
        var result = await uploads.SaveAsync(input, cancellationToken);
        return result.Created
            ? Created($"/api/artifacts/{result.Artifact.Id}", result.Artifact.ToResponse())
            : Ok(result.Artifact.ToResponse());
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ArtifactResponse>>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        var artifacts = await artifactService.GetByProjectIdAsync(projectId, cancellationToken);
        var response = artifacts.Select(artifact => artifact.ToResponse()).ToArray();

        return Ok(response);
    }

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<ArtifactListItemResponse>>> GetHistoryAsync(
        string projectId,
        CancellationToken cancellationToken,
        int skip = 0,
        int take = 50
    )
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
            return NotFound(new { error = "Project was not found." });

        return Ok(
            await artifactService.GetHistoryAsync(project.Id, null, skip, take, cancellationToken)
        );
    }

    [HttpGet("compare")]
    public async Task<ActionResult<ArtifactComparisonResponse>> CompareAsync(
        string projectId,
        [FromQuery(Name = "from")] string? fromVersion,
        [FromQuery(Name = "to")] string? toVersion,
        CancellationToken cancellationToken
    )
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        if (string.IsNullOrWhiteSpace(fromVersion))
        {
            return BadRequest(new { error = "The from version is required." });
        }

        if (string.IsNullOrWhiteSpace(toVersion))
        {
            return BadRequest(new { error = "The to version is required." });
        }

        var comparison = await artifactService.CompareVersionsAsync(
            projectId,
            fromVersion,
            toVersion,
            cancellationToken
        );

        return comparison is null
            ? NotFound(new { error = "One or both artifact versions were not found." })
            : Ok(comparison);
    }
}
