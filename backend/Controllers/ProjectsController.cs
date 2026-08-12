using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Controllers;

[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(
    ProjectService projectService,
    ProjectCreationSignatureValidator projectCreationSignatureValidator,
    ProjectManagementSignatureValidator projectManagementSignatureValidator,
    SignatureNonceService nonceService) : ControllerBase
{
    [HttpPost]
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

        var idempotencyKey = ReadIdempotencyKey();
        if (idempotencyKey is null && Request.Headers.ContainsKey("Idempotency-Key"))
        {
            return BadRequest(new { error = "Idempotency-Key must be between 1 and 128 characters." });
        }
        if (idempotencyKey is not null)
        {
            var existing = await projectService.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return Ok(existing.ToResponse());
            }
        }

        var signatureValidation = projectCreationSignatureValidator.Validate(request);
        if (!signatureValidation.IsValid)
        {
            return BadRequest(new { error = signatureValidation.Error });
        }
        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "This wallet signature has already been used." });
        }

        try
        {
            var project = await projectService.CreateAsync(request, idempotencyKey, cancellationToken);

            return Created($"/api/projects/{project.Id}", project.ToResponse());
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey && idempotencyKey is not null)
        {
            var existing = await projectService.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
            return existing is null ? Conflict() : Ok(existing.ToResponse());
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectResponse>>> GetAllAsync(CancellationToken cancellationToken)
    {
        var projects = await projectService.GetAllAsync(cancellationToken);
        var response = projects.Select(project => project.ToResponse()).ToArray();

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

        var signatureValidation = projectManagementSignatureValidator.ValidateProjectDeletion(
            project,
            request.ProjectName,
            request.Signature);
        if (!signatureValidation.IsValid)
        {
            return signatureValidation.Error.StartsWith("Only the project creator", StringComparison.Ordinal)
                ? StatusCode(StatusCodes.Status403Forbidden, new { error = signatureValidation.Error })
                : BadRequest(new { error = signatureValidation.Error });
        }

        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "This wallet signature has already been used." });
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
