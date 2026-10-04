using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/authorized-deployers")]
public sealed class ProjectAuthorizedDeployersController(
    ProjectService projects,
    ProjectAuthorizedDeployerInputValidator inputValidator,
    ProjectAuthorizedDeployerService authorizedDeployerService,
    CurrentWalletSessionAccessor walletSession,
    ProjectAuthorizationService authorization) : ControllerBase
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
    [RequireWalletSession]
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
        var access = authorization.CanAdministerProject(project, walletSession.Current!.Address);
        if (!access.IsAllowed)
        {
            return StatusCode(access.StatusCode, new { error = access.Error });
        }

        if (string.Equals(project.CreatedByWalletAddress, input.WalletAddress, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "The project owner cannot be added as an authorized deployer." });
        }
        var existing = project.AuthorizedDeployers.FirstOrDefault(item => item.WalletAddress == input.WalletAddress);
        if (existing is not null)
        {
            return existing.AllowedNetworks.Order(StringComparer.Ordinal).SequenceEqual(input.AllowedNetworks)
                ? Ok(existing.ToResponse())
                : Conflict(new { error = "This wallet is already an authorized deployer with different network access." });
        }
        if (project.AuthorizedDeployers.Count >= ProjectAuthorizedDeployerInputValidator.MaxAuthorizedDeployers)
        {
            return Conflict(new { error = $"A project can have at most {ProjectAuthorizedDeployerInputValidator.MaxAuthorizedDeployers} authorized deployers." });
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
    [RequireWalletSession]
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
        var access = authorization.CanAdministerProject(project, walletSession.Current!.Address);
        if (!access.IsAllowed)
        {
            return StatusCode(access.StatusCode, new { error = access.Error });
        }
        var existing = project.AuthorizedDeployers.FirstOrDefault(item => item.WalletAddress == input.WalletAddress);
        if (existing is null)
        {
            return NotFound(new { error = "Authorized deployer was not found." });
        }
        if (existing.AllowedNetworks.Order(StringComparer.Ordinal).SequenceEqual(input.AllowedNetworks))
        {
            return Ok(existing.ToResponse());
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
    [RequireWalletSession]
    public async Task<IActionResult> RemoveAsync(
        string projectId,
        string walletAddress,
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
        var access = authorization.CanAdministerProject(project, walletSession.Current!.Address);
        if (!access.IsAllowed)
        {
            return StatusCode(access.StatusCode, new { error = access.Error });
        }
        var existing = project.AuthorizedDeployers.FirstOrDefault(item => item.WalletAddress == input.WalletAddress);
        if (existing is null)
        {
            return NoContent();
        }

        var updatedProject = await authorizedDeployerService.RemoveAsync(projectId, existing.WalletAddress, cancellationToken);
        return updatedProject is null
            ? Conflict(new { error = "The authorized deployer was already removed. Reload the list and try again." })
            : NoContent();
    }
}
