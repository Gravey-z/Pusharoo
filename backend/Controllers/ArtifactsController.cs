using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/artifacts")]
public sealed class ArtifactsController(ArtifactService artifactService) : ControllerBase
{
    [HttpGet("{artifactId}")]
    public async Task<ActionResult<ArtifactResponse>> GetByIdAsync(
        string artifactId,
        CancellationToken cancellationToken)
    {
        var artifact = await artifactService.GetByIdAsync(artifactId, cancellationToken);

        return artifact is null ? NotFound() : Ok(artifact.ToResponse());
    }

    [HttpGet("{artifactId}/manifest")]
    public async Task<IActionResult> GetManifestAsync(
        string artifactId,
        CancellationToken cancellationToken)
    {
        var artifact = await artifactService.GetByIdAsync(artifactId, cancellationToken);

        return artifact is null ? NotFound() : Ok(artifact.Manifest);
    }

    [HttpGet("{artifactId}/nef")]
    public async Task<IActionResult> GetNefAsync(
        string artifactId,
        CancellationToken cancellationToken)
    {
        var artifact = await artifactService.GetByIdAsync(artifactId, cancellationToken);

        return artifact is null
            ? NotFound()
            : File(artifact.Nef, "application/octet-stream", artifact.NefFileName);
    }

    [HttpGet("{artifactId}/summary")]
    public async Task<ActionResult<ArtifactSummary>> GetSummaryAsync(
        string artifactId,
        CancellationToken cancellationToken)
    {
        var artifact = await artifactService.GetByIdAsync(artifactId, cancellationToken);

        return artifact is null ? NotFound() : Ok(artifact.Summary);
    }
}
