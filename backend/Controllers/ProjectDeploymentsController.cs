using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/deployments")]
public sealed class ProjectDeploymentsController(
    DeploymentLifecycleService lifecycle,
    DeploymentRecoveryService recovery,
    CurrentWalletSessionAccessor currentWallet
) : ControllerBase
{
    [HttpPost]
    public Task<ActionResult<DeploymentResponse>> CreateAsync(
        string projectId,
        CreateDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<ActionResult<DeploymentResponse>>(
            Conflict(
                new
                {
                    error = "Direct deployment recording is disabled. Start an authorized deployment attempt instead.",
                }
            )
        );

    [HttpPost("review")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentReviewResponse>> ReviewAsync(
        string projectId,
        DeploymentReviewRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            await lifecycle.ReviewAsync(
                projectId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeploymentResponse>>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken
    ) => Respond(projectId, await lifecycle.GetAllAsync(projectId, cancellationToken));

    [HttpPost("attempts")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> StartAttemptAsync(
        string projectId,
        StartSessionDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            await lifecycle.StartAttemptAsync(
                projectId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpPost("{deploymentId}/submitted")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> MarkSubmittedAsync(
        string projectId,
        string deploymentId,
        SubmitDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            await lifecycle.MarkSubmittedAsync(
                projectId,
                deploymentId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpPost("{deploymentId}/confirm")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> ConfirmAttemptAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            await lifecycle.ConfirmAttemptAsync(
                projectId,
                deploymentId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpPost("{deploymentId}/failed")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> MarkFailedAsync(
        string projectId,
        string deploymentId,
        FailDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            await lifecycle.MarkFailedAsync(
                projectId,
                deploymentId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpPost("{deploymentId}/resume")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> ResumeAttemptAsync(
        string projectId,
        string deploymentId,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            await lifecycle.ResumeAttemptAsync(
                projectId,
                deploymentId,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpPost("recover")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> RecoverAsync(
        string projectId,
        RecoverDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            await recovery.RecoverAsync(
                projectId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    private ActionResult Respond<T>(string projectId, DeploymentWorkflowResult<T> result)
    {
        if (result.IsSuccess)
        {
            return
                result.StatusCode == StatusCodes.Status201Created
                && result.Value is DeploymentResponse deployment
                ? Created($"/api/projects/{projectId}/deployments/{deployment.Id}", deployment)
                : Ok(result.Value);
        }

        object body = result.RelatedDeployment is null
            ? new { error = result.Error }
            : new { error = result.Error, deployment = result.RelatedDeployment };

        return StatusCode(result.StatusCode, body);
    }
}
