using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/contracts/{contractId}/artifacts")]
public sealed class ContractArtifactsController(
    ProjectContractLoader loader,
    ContractArtifactService contractArtifacts,
    ArtifactUploadReader uploadReader,
    ArtifactService artifacts,
    CurrentWalletSessionAccessor walletSession
) : ControllerBase
{
    [HttpPost]
    [RequireWalletSession]
    [Consumes("multipart/form-data")]
    [DisableFormValueModelBinding]
    public async Task<ActionResult<ArtifactResponse>> UploadAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    )
    {
        var actor = walletSession.Current!;
        var context = await contractArtifacts.LoadForUploadAsync(
            projectId,
            contractId,
            actor.Address,
            cancellationToken
        );
        var input = await uploadReader.ReadAsync(
            Request,
            context.Project.Id,
            context.Contract.Id,
            actor.Address,
            cancellationToken
        );
        var result = await contractArtifacts.UploadAsync(input, actor.Address, cancellationToken);

        return result.Created
            ? Created($"/api/artifacts/{result.Artifact.Id}", result.Artifact.ToResponse())
            : Ok(result.Artifact.ToResponse());
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ArtifactListItemResponse>>> ListAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken,
        int skip = 0,
        int take = 50
    )
    {
        var context = await loader.LoadAsync(projectId, contractId, cancellationToken);
        return Ok(
            await artifacts.GetHistoryAsync(
                context.Project.Id,
                context.Contract.Id,
                skip,
                take,
                cancellationToken
            )
        );
    }

    [HttpGet("compare")]
    public async Task<ActionResult<ArtifactComparisonResponse>> CompareAsync(
        string projectId,
        string contractId,
        [FromQuery(Name = "from")] string? fromVersion,
        [FromQuery(Name = "to")] string? toVersion,
        CancellationToken cancellationToken
    )
    {
        var context = await loader.LoadAsync(projectId, contractId, cancellationToken);
        if (string.IsNullOrWhiteSpace(fromVersion) || string.IsNullOrWhiteSpace(toVersion))
            return BadRequest(new { error = "Both from and to versions are required." });

        var comparison = await artifacts.CompareContractVersionsAsync(
            context.Project.Id,
            context.Contract.Id,
            fromVersion,
            toVersion,
            cancellationToken
        );
        return comparison is null
            ? NotFound(
                new { error = "One or both artifact versions were not found in this contract." }
            )
            : Ok(comparison);
    }
}
