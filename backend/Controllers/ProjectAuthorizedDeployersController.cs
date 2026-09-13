using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/authorized-deployers")]
public sealed class ProjectAuthorizedDeployersController(
    ProjectService projects,
    ProjectAuthorizedDeployerInputValidator inputValidator,
    ProjectAuthorizedDeployerSignatureValidator signatureValidator,
    ProjectAuthorizedDeployerService authorizedDeployerService,
    SignatureNonceService nonceService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectAuthorizedDeployerResponse>>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        return project is null
            ? NotFound(new { error = "Project was not found." })
            : Ok(project.AuthorizedDeployers.Select(authorizedDeployer => authorizedDeployer.ToResponse()).ToArray());
    }

    [HttpPost]
    public async Task<ActionResult<ProjectAuthorizedDeployerResponse>> AddAsync(
        string projectId,
        AddProjectAuthorizedDeployerRequest request,
        CancellationToken cancellationToken)
    {
        var input = inputValidator.ValidateAdd(request);
        if (!input.IsValid)
        {
            return BadRequest(new { error = input.Error });
        }

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        if (string.Equals(project.CreatedByWalletAddress, input.WalletAddress, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "The project owner cannot be added as an authorized deployer." });
        }
        if (project.AuthorizedDeployers.Count >= ProjectAuthorizedDeployerInputValidator.MaxAuthorizedDeployers)
        {
            return Conflict(new { error = $"A project can have at most {ProjectAuthorizedDeployerInputValidator.MaxAuthorizedDeployers} authorized deployers." });
        }
        if (project.AuthorizedDeployers.Any(item => item.WalletAddress == input.WalletAddress))
        {
            return Conflict(new { error = "This wallet is already an authorized deployer." });
        }

        var signature = signatureValidator.ValidateAdd(project, input, request.Signature);
        if (!signature.IsValid)
        {
            return StatusCode(signature.StatusCode, new { error = signature.Error });
        }
        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "This authorized-deployer signature has already been used." });
        }

        var now = DateTime.UtcNow;
        var authorizedDeployer = new ProjectAuthorizedDeployerDocument
        {
            WalletAddress = input.WalletAddress,
            ScriptHash = input.ScriptHash,
            AllowedNetworks = input.AllowedNetworks,
            AddedAtUtc = now,
            UpdatedAtUtc = now
        };
        var updatedProject = await authorizedDeployerService.AddAsync(projectId, authorizedDeployer, cancellationToken);
        if (updatedProject is null)
        {
            return Conflict(new { error = "The authorized-deployer list changed. Reload it and try again." });
        }

        return Created($"/api/projects/{projectId}/authorized-deployers/{Uri.EscapeDataString(authorizedDeployer.WalletAddress)}", authorizedDeployer.ToResponse());
    }

    [HttpPut("{walletAddress}")]
    public async Task<ActionResult<ProjectAuthorizedDeployerResponse>> UpdateAsync(
        string projectId,
        string walletAddress,
        UpdateProjectAuthorizedDeployerRequest request,
        CancellationToken cancellationToken)
    {
        var input = inputValidator.ValidateUpdate(walletAddress, request);
        if (!input.IsValid)
        {
            return BadRequest(new { error = input.Error });
        }

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }
        var existing = project.AuthorizedDeployers.FirstOrDefault(item => item.WalletAddress == input.WalletAddress);
        if (existing is null)
        {
            return NotFound(new { error = "Authorized deployer was not found." });
        }

        var signature = signatureValidator.ValidateUpdate(project, input, request.Signature);
        if (!signature.IsValid)
        {
            return StatusCode(signature.StatusCode, new { error = signature.Error });
        }
        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "This authorized-deployer signature has already been used." });
        }

        var authorizedDeployer = existing with
        {
            AllowedNetworks = input.AllowedNetworks,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var updatedProject = await authorizedDeployerService.UpdateAsync(projectId, authorizedDeployer, cancellationToken);
        return updatedProject is null
            ? Conflict(new { error = "The authorized deployer was removed before this change was saved. Reload the list and try again." })
            : Ok(authorizedDeployer.ToResponse());
    }

    [HttpDelete("{walletAddress}")]
    public async Task<IActionResult> RemoveAsync(
        string projectId,
        string walletAddress,
        RemoveProjectAuthorizedDeployerRequest request,
        CancellationToken cancellationToken)
    {
        var input = inputValidator.ValidateRemove(walletAddress);
        if (!input.IsValid)
        {
            return BadRequest(new { error = input.Error });
        }

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }
        var existing = project.AuthorizedDeployers.FirstOrDefault(item => item.WalletAddress == input.WalletAddress);
        if (existing is null)
        {
            return NotFound(new { error = "Authorized deployer was not found." });
        }

        var signature = signatureValidator.ValidateRemove(project, existing, request.Signature);
        if (!signature.IsValid)
        {
            return StatusCode(signature.StatusCode, new { error = signature.Error });
        }
        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "This authorized-deployer signature has already been used." });
        }

        var updatedProject = await authorizedDeployerService.RemoveAsync(projectId, existing.WalletAddress, cancellationToken);
        return updatedProject is null
            ? Conflict(new { error = "The authorized deployer was already removed. Reload the list and try again." })
            : NoContent();
    }
}
