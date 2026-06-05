using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace backend.Controllers;

[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(
    ProjectService projectService,
    ArtifactService artifactService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ProjectResponse>> CreateAsync(
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Project name is required." });
        }

        var project = await projectService.CreateAsync(request, cancellationToken);

        return Created($"/api/projects/{project.Id}", project.ToResponse());
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectResponse>>> GetAllAsync(CancellationToken cancellationToken)
    {
        var projects = await projectService.GetAllAsync(cancellationToken);
        var response = projects.Select(project => project.ToResponse()).ToArray();

        return Ok(response);
    }

    [HttpGet("{projectId}")]
    public async Task<ActionResult<ProjectResponse>> GetByIdAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);

        return project is null ? NotFound() : Ok(project.ToResponse());
    }

    [HttpPost("{projectId}/artifacts")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ArtifactResponse>> UploadArtifactAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        if (!Request.HasFormContentType)
        {
            return StatusCode(
                StatusCodes.Status415UnsupportedMediaType,
                new { error = "Expected multipart/form-data." });
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        var version = form["version"].FirstOrDefault();
        var notes = form["notes"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(version))
        {
            return BadRequest(new { error = "Version is required." });
        }

        var nefFile = FindNefFile(form.Files);
        if (nefFile is null)
        {
            return BadRequest(new { error = "A .nef file is required." });
        }

        var manifestFile = FindManifestFile(form.Files);
        if (manifestFile is null)
        {
            return BadRequest(new { error = "A contract manifest JSON file is required." });
        }

        var nefBytes = await ReadBytesAsync(nefFile, cancellationToken);
        var manifestJson = await ReadTextAsync(manifestFile, cancellationToken);

        try
        {
            var artifact = await artifactService.CreateAsync(
                new ArtifactUploadInput(
                    projectId,
                    version,
                    notes,
                    nefFile.FileName,
                    nefBytes,
                    manifestJson),
                cancellationToken);

            return Created($"/api/artifacts/{artifact.Id}", artifact.ToResponse());
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "The manifest file must contain valid JSON." });
        }
    }

    [HttpGet("{projectId}/artifacts")]
    public async Task<ActionResult<IReadOnlyList<ArtifactResponse>>> GetArtifactsAsync(
        string projectId,
        CancellationToken cancellationToken)
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

    [HttpGet("{projectId}/artifacts/compare")]
    public async Task<ActionResult<ArtifactComparisonResponse>> CompareArtifactsAsync(
        string projectId,
        [FromQuery(Name = "from")] string? fromVersion,
        [FromQuery(Name = "to")] string? toVersion,
        CancellationToken cancellationToken)
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
            cancellationToken);

        return comparison is null
            ? NotFound(new { error = "One or both artifact versions were not found." })
            : Ok(comparison);
    }

    private static IFormFile? FindNefFile(IFormFileCollection files)
    {
        return files.FirstOrDefault(file =>
            file.FileName.EndsWith(".nef", StringComparison.OrdinalIgnoreCase));
    }

    private static IFormFile? FindManifestFile(IFormFileCollection files)
    {
        return files.FirstOrDefault(file =>
            file.FileName.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(file =>
                file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<byte[]> ReadBytesAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var memoryStream = new MemoryStream();

        await stream.CopyToAsync(memoryStream, cancellationToken);

        return memoryStream.ToArray();
    }

    private static async Task<string> ReadTextAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(cancellationToken);
    }
}
