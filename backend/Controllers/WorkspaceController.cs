using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}")]
public sealed class WorkspaceController(WorkspaceReadService workspace) : ControllerBase
{
    [HttpGet("workspace")]
    public async Task<ActionResult<ProjectWorkspaceResponse>> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken
    ) => Ok(await workspace.GetProjectAsync(projectId, cancellationToken));

    [HttpGet("contracts/{contractId}/overview")]
    public async Task<ActionResult<ContractOverviewResponse>> GetContractAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    ) => Ok(await workspace.GetContractAsync(projectId, contractId, cancellationToken));
}
