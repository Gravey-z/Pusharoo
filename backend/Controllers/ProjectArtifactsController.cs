using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/artifacts")]
public sealed class ProjectArtifactsController(
    ProjectService projectService,
    ArtifactService artifactService,
    ProjectManagementSignatureValidator projectManagementSignatureValidator) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost]
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
        var signature = ReadWalletSignature(form);
        if (!signature.IsValid)
        {
            return BadRequest(new { error = signature.Error });
        }

        var signatureValidation = projectManagementSignatureValidator.ValidateArtifactUpload(
            project,
            version,
            notes,
            nefBytes,
            manifestJson,
            signature.Signature);
        if (!signatureValidation.IsValid)
        {
            return SignatureError(signatureValidation.Error);
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
                    manifestJson),
                cancellationToken);

            return Created($"/api/artifacts/{artifact.Id}", artifact.ToResponse());
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "The manifest file must contain valid JSON." });
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

    private ActionResult ForbidWithError(string error)
    {
        return StatusCode(StatusCodes.Status403Forbidden, new { error });
    }

    private ActionResult SignatureError(string error)
    {
        return error.StartsWith("Only the project creator", StringComparison.Ordinal)
            ? ForbidWithError(error)
            : BadRequest(new { error });
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

    private static WalletSignatureFormResult ReadWalletSignature(IFormCollection form)
    {
        var signatureJson = form["signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(signatureJson))
        {
            return new WalletSignatureFormResult(null, false, "Wallet signature is required.");
        }

        try
        {
            var signature = JsonSerializer.Deserialize<WalletSignatureRequest>(signatureJson, JsonOptions);

            return signature is null
                ? new WalletSignatureFormResult(null, false, "Wallet signature is invalid.")
                : new WalletSignatureFormResult(signature, true, string.Empty);
        }
        catch (JsonException)
        {
            return new WalletSignatureFormResult(null, false, "Wallet signature must be valid JSON.");
        }
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

    private sealed record WalletSignatureFormResult(
        WalletSignatureRequest? Signature,
        bool IsValid,
        string Error);
}
