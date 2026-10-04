using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.Text.Json;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/artifacts")]
public sealed class ProjectArtifactsController(
    ProjectService projectService,
    ArtifactService artifactService,
    CurrentWalletSessionAccessor walletSession,
    ProjectAuthorizationService authorization) : ControllerBase
{
    [HttpPost]
    [RequireWalletSession]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ArtifactResponse>> UploadAsync(
        string projectId,
        CancellationToken cancellationToken)
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

        if (!Request.HasFormContentType)
        {
            return StatusCode(
                StatusCodes.Status415UnsupportedMediaType,
                new { error = "Expected multipart/form-data." });
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        var version = form["version"].FirstOrDefault();
        var notes = form["notes"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(version) || version.Trim().Length > 64)
        {
            return BadRequest(new { error = "Version is required and must be at most 64 characters." });
        }
        if (notes?.Length > 2_000)
        {
            return BadRequest(new { error = "Notes must be at most 2000 characters." });
        }

        var idempotencyKey = ReadIdempotencyKey();
        if (idempotencyKey is null && Request.Headers.ContainsKey("Idempotency-Key"))
        {
            return BadRequest(new { error = "Idempotency-Key must be between 1 and 128 characters." });
        }
        var scopedIdempotencyKey = idempotencyKey is null ? null
            : WorkspaceIdempotency.Scope(walletSession.Current!.Address, projectId, "artifact.upload", idempotencyKey);

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
        if (nefFile.Length is 0 or > 8 * 1024 * 1024 || manifestFile.Length is 0 or > 1024 * 1024)
        {
            return BadRequest(new { error = "NEF files must be at most 8 MB and manifest files at most 1 MB." });
        }

        var nefBytes = await ReadBytesAsync(nefFile, cancellationToken);
        var manifestJson = await ReadTextAsync(manifestFile, cancellationToken);
        var payloadHash = WorkspaceIdempotency.Hash(version.Trim(),
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Path.GetFileName(nefFile.FileName), WorkspaceIdempotency.HashBytes(nefBytes), manifestJson);
        if (scopedIdempotencyKey is not null)
        {
            var existing = await artifactService.GetByIdempotencyKeyAsync(scopedIdempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return existing.IdempotencyPayloadHash == payloadHash
                    ? Ok(existing.ToResponse())
                    : Conflict(new { error = "Idempotency-Key was already used with different artifact data." });
            }
        }

        try
        {
            var artifact = await artifactService.CreateAsync(
                new ArtifactUploadInput(
                    projectId,
                    version,
                    notes,
                    nefFile.FileName,
                    nefBytes,
                    manifestJson,
                    scopedIdempotencyKey,
                    payloadHash),
                cancellationToken);

            return Created($"/api/artifacts/{artifact.Id}", artifact.ToResponse());
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "The manifest file must contain valid JSON." });
        }
        catch (ArtifactValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            if (scopedIdempotencyKey is not null)
            {
                var existing = await artifactService.GetByIdempotencyKeyAsync(scopedIdempotencyKey, cancellationToken);
                if (existing is not null)
                {
                    return existing.IdempotencyPayloadHash == payloadHash
                        ? Ok(existing.ToResponse())
                        : Conflict(new { error = "Idempotency-Key was already used with different artifact data." });
                }
            }
            return Conflict(new { error = "An artifact with this project version or idempotency key already exists." });
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ArtifactResponse>>> GetAllAsync(
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

    [HttpGet("compare")]
    public async Task<ActionResult<ArtifactComparisonResponse>> CompareAsync(
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

    private string? ReadIdempotencyKey()
    {
        var value = Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > 128 ? null : value;
    }

}
