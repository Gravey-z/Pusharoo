using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/webhook-access")]
public sealed class WebhookAccessController(
    ProjectService projectService,
    ProjectManagementSignatureValidator signatureValidator,
    WebhookAuthorizationNonceService nonceService) : ControllerBase
{
    [HttpPost("validate")]
    public async Task<IActionResult> ValidateAsync(
        string projectId,
        WebhookAccessValidationRequest request,
        CancellationToken cancellationToken)
    {
        var project = await projectService.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { error = "Project was not found." });
        }

        var validation = signatureValidator.ValidateWebhookAdministration(
            project,
            request.Operation,
            request.RequestHash,
            request.Signature);

        if (!validation.IsValid)
        {
            var statusCode = validation.Error.StartsWith("Only the project creator", StringComparison.Ordinal)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status400BadRequest;

            return StatusCode(statusCode, new { error = validation.Error });
        }

        if (!await nonceService.TryConsumeAsync(request.Signature!, cancellationToken))
        {
            return Conflict(new { error = "Webhook management signature has already been used." });
        }

        return NoContent();
    }
}
