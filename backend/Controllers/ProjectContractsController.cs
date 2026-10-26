using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/contracts")]
public sealed class ProjectContractsController(
    ContractService contracts,
    CurrentWalletSessionAccessor walletSession
) : ControllerBase
{
    [HttpPost]
    [RequireWalletSession]
    public async Task<ActionResult<ContractResponse>> CreateAsync(
        string projectId,
        CreateContractRequest request,
        CancellationToken cancellationToken
    )
    {
        var idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var key)
            ? key.ToString()
            : null;
        var contract = await contracts.CreateAsync(
            projectId,
            request,
            walletSession.Current!.Address,
            idempotencyKey,
            cancellationToken
        );

        return Created($"/api/projects/{projectId}/contracts/{contract.Id}", contract.ToResponse());
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ContractResponse>>> ListAsync(
        string projectId,
        CancellationToken cancellationToken,
        int skip = 0,
        int take = 50
    )
    {
        var items = await contracts.ListAsync(projectId, skip, take, cancellationToken);
        return Ok(items.Select(contract => contract.ToResponse()).ToArray());
    }

    [HttpGet("{contractId}")]
    public async Task<ActionResult<ContractResponse>> GetAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    ) => Ok((await contracts.GetAsync(projectId, contractId, cancellationToken)).ToResponse());

    [HttpPatch("{contractId}")]
    [RequireWalletSession]
    public async Task<ActionResult<ContractResponse>> RenameAsync(
        string projectId,
        string contractId,
        RenameContractRequest request,
        CancellationToken cancellationToken
    ) =>
        Ok(
            (
                await contracts.RenameAsync(
                    projectId,
                    contractId,
                    request,
                    walletSession.Current!.Address,
                    cancellationToken
                )
            ).ToResponse()
        );
}
