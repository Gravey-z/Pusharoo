using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(
    ProjectService projectService,
    ProjectCreationSignatureValidator projectCreationSignatureValidator) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ProjectResponse>> CreateAsync(
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Project name is required." });
        }

        var signatureValidation = projectCreationSignatureValidator.Validate(request);
        if (!signatureValidation.IsValid)
        {
            return BadRequest(new { error = signatureValidation.Error });
        }

        var project = await projectService.CreateAsync(request, cancellationToken);

        return Created($"/api/projects/{project.Id}", project.ToResponse());
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
}
