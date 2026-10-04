using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Controllers;

[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(
    ProjectService projectService,
    CurrentWalletSessionAccessor walletSession,
    ProjectAuthorizationService authorization) : ControllerBase
{
    [HttpPost]
    [RequireWalletSession]
    public async Task<ActionResult<ProjectResponse>> CreateAsync(
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
        {
            return BadRequest(new { error = "Project name is required and must be at most 128 characters." });
        }
        if (request.Description?.Length > 2_000)
        {
            return BadRequest(new { error = "Project description must be at most 2000 characters." });
        }
        if (request.CreatorNetwork is not ("neo3:testnet" or "neo3:mainnet"))
        {
            return BadRequest(new { error = "Choose Neo N3 TestNet or MainNet." });
        }

        var actor = walletSession.Current!;
        var payloadHash = WorkspaceIdempotency.Hash(request.Name.Trim(),
            string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), request.CreatorNetwork);

        var idempotencyKey = ReadIdempotencyKey();
        if (idempotencyKey is null && Request.Headers.ContainsKey("Idempotency-Key"))
        {
            return BadRequest(new { error = "Idempotency-Key must be between 1 and 128 characters." });
        }
        idempotencyKey = idempotencyKey is null ? null
            : WorkspaceIdempotency.Scope(actor.Address, string.Empty, "project.create", idempotencyKey);
        if (idempotencyKey is not null)
        {
            var existing = await projectService.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return existing.IdempotencyPayloadHash == payloadHash
                    ? Ok(existing.ToResponse())
                    : Conflict(new { error = "Idempotency-Key was already used with a different project request." });
            }
        }

        try
        {
            var project = await projectService.CreateAsync(request, actor, idempotencyKey, payloadHash, cancellationToken);

            return Created($"/api/projects/{project.Id}", project.ToResponse());
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey && idempotencyKey is not null)
        {
            var existing = await projectService.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
            return existing?.IdempotencyPayloadHash == payloadHash
                ? Ok(existing.ToResponse())
                : Conflict(new { error = "Idempotency-Key was already used with a different project request." });
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectResponse>>> GetAllAsync(CancellationToken cancellationToken)
    {
        var projects = await projectService.GetAllAsync(cancellationToken);
        var response = projects.Select(project => project.ToResponse()).ToArray();

        return Ok(response);
    }

    [HttpGet("cards")]
    public async Task<ActionResult<IReadOnlyList<ProjectListItemResponse>>> GetCardsAsync(CancellationToken cancellationToken)
    {
        var response = await projectService.GetListItemsAsync(cancellationToken);
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

    [HttpDelete("{projectId}")]
    [RequireWalletSession]
    public async Task<IActionResult> DeleteAsync(
        string projectId,
        DeleteProjectRequest request,
        CancellationToken cancellationToken)
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        var access = authorization.CanAdministerProject(project, walletSession.Current!.Address);
        if (!access.IsAllowed)
        {
            return StatusCode(access.StatusCode, new { error = access.Error });
        }
        if (!string.Equals(project.Name, request.ProjectName?.Trim(), StringComparison.Ordinal))
        {
            return BadRequest(new { error = "Type the exact project name to confirm deletion." });
        }

        await projectService.DeleteAsync(projectId, cancellationToken);
        return NoContent();
    }

    private string? ReadIdempotencyKey()
    {
        var value = Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > 128 ? null : value;
    }
}
