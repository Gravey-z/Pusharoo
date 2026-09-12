using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/collaborators")]
public sealed class ProjectCollaboratorsController(
    ProjectService projects,
    ProjectCollaboratorInputValidator inputValidator,
    ProjectCollaboratorSignatureValidator signatureValidator,
    ProjectCollaborationService collaboration,
    SignatureNonceService nonceService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectCollaboratorResponse>>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        return project is null
            ? NotFound(new { error = "Project was not found." })
            : Ok(project.Collaborators.Select(collaborator => collaborator.ToResponse()).ToArray());
    }

    [HttpGet("audit")]
    public async Task<ActionResult<IReadOnlyList<ProjectAccessAuditResponse>>> GetAuditAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        return project is null
            ? NotFound(new { error = "Project was not found." })
            : Ok(project.AccessAuditEvents
                .OrderByDescending(item => item.CreatedAtUtc)
                .Select(item => item.ToResponse())
                .ToArray());
    }

    [HttpPost]
    public async Task<ActionResult<ProjectCollaboratorResponse>> AddAsync(
        string projectId,
        AddProjectCollaboratorRequest request,
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
            return BadRequest(new { error = "The project owner cannot be added as a collaborator." });
        }
        if (project.Collaborators.Count >= ProjectCollaboratorInputValidator.MaxCollaborators)
        {
            return Conflict(new { error = $"A project can have at most {ProjectCollaboratorInputValidator.MaxCollaborators} collaborators." });
        }
        if (project.Collaborators.Any(item => item.WalletAddress == input.WalletAddress))
        {
            return Conflict(new { error = "This wallet is already a collaborator. Reload the latest grant before editing it." });
        }

        var signature = signatureValidator.ValidateAdd(project, input, request.ExpectedGrantRevision, request.Signature);
        if (!signature.IsValid)
        {
            return StatusCode(signature.StatusCode, new { error = signature.Error });
        }
        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "This collaborator authorization signature has already been used." });
        }

        var now = DateTime.UtcNow;
        var collaborator = new ProjectCollaboratorDocument
        {
            WalletAddress = input.WalletAddress,
            ScriptHash = input.ScriptHash,
            Role = input.Role,
            AllowedNetworks = input.AllowedNetworks,
            GrantRevision = 1,
            AddedAtUtc = now,
            AddedByWalletAddress = request.Signature!.Address.Trim(),
            UpdatedAtUtc = now,
            UpdatedByWalletAddress = request.Signature.Address.Trim()
        };
        var audit = ProjectCollaborationService.CreateAuditEvent(
            "collaborator.added",
            request.Signature.Address.Trim(),
            collaborator.WalletAddress,
            null,
            collaborator,
            request.Signature);
        var updatedProject = await collaboration.AddAsync(projectId, collaborator, audit, cancellationToken);
        if (updatedProject is null)
        {
            return Conflict(new { error = "The collaborator list changed. Reload the project and sign a new request." });
        }

        return Created($"/api/projects/{projectId}/collaborators/{Uri.EscapeDataString(collaborator.WalletAddress)}", collaborator.ToResponse());
    }

    [HttpPut("{walletAddress}")]
    public async Task<ActionResult<ProjectCollaboratorResponse>> UpdateAsync(
        string projectId,
        string walletAddress,
        UpdateProjectCollaboratorRequest request,
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

        var existing = project.Collaborators.FirstOrDefault(item => item.WalletAddress == input.WalletAddress);
        if (existing is null)
        {
            return NotFound(new { error = "Collaborator was not found." });
        }
        if (existing.GrantRevision != request.ExpectedGrantRevision)
        {
            return Conflict(new { error = "The collaborator grant has changed. Reload it and sign a new request." });
        }

        var signature = signatureValidator.ValidateUpdate(project, input, request.ExpectedGrantRevision, request.Signature);
        if (!signature.IsValid)
        {
            return StatusCode(signature.StatusCode, new { error = signature.Error });
        }
        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "This collaborator authorization signature has already been used." });
        }

        var collaborator = existing with
        {
            Role = input.Role,
            AllowedNetworks = input.AllowedNetworks,
            GrantRevision = existing.GrantRevision + 1,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByWalletAddress = request.Signature!.Address.Trim()
        };
        var audit = ProjectCollaborationService.CreateAuditEvent(
            "collaborator.updated",
            request.Signature.Address.Trim(),
            collaborator.WalletAddress,
            existing,
            collaborator,
            request.Signature);
        var updatedProject = await collaboration.UpdateAsync(projectId, collaborator, request.ExpectedGrantRevision, audit, cancellationToken);
        if (updatedProject is null)
        {
            return Conflict(new { error = "The collaborator grant has changed. Reload it and sign a new request." });
        }

        return Ok(collaborator.ToResponse());
    }

    [HttpDelete("{walletAddress}")]
    public async Task<IActionResult> RemoveAsync(
        string projectId,
        string walletAddress,
        RemoveProjectCollaboratorRequest request,
        CancellationToken cancellationToken)
    {
        var input = inputValidator.ValidateRemove(walletAddress, request);
        if (!input.IsValid)
        {
            return BadRequest(new { error = input.Error });
        }

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        var existing = project.Collaborators.FirstOrDefault(item => item.WalletAddress == input.WalletAddress);
        if (existing is null)
        {
            return NotFound(new { error = "Collaborator was not found." });
        }
        if (existing.GrantRevision != request.ExpectedGrantRevision)
        {
            return Conflict(new { error = "The collaborator grant has changed. Reload it and sign a new request." });
        }

        var signature = signatureValidator.ValidateRemove(project, input, existing, request.ExpectedGrantRevision, request.Signature);
        if (!signature.IsValid)
        {
            return StatusCode(signature.StatusCode, new { error = signature.Error });
        }
        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "This collaborator authorization signature has already been used." });
        }

        var audit = ProjectCollaborationService.CreateAuditEvent(
            "collaborator.removed",
            request.Signature!.Address.Trim(),
            existing.WalletAddress,
            existing,
            null,
            request.Signature);
        var updatedProject = await collaboration.RemoveAsync(projectId, existing.WalletAddress, request.ExpectedGrantRevision, audit, cancellationToken);
        return updatedProject is null
            ? Conflict(new { error = "The collaborator grant has changed. Reload it and sign a new request." })
            : NoContent();
    }
}
