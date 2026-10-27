using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/projects/{projectId}/contracts/{contractId}/deployments")]
public sealed class ContractDeploymentsController(
    ContractDeploymentService lifecycle,
    CurrentWalletSessionAccessor currentWallet
) : ControllerBase
{
    [HttpPost("review")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentReviewResponse>> ReviewAsync(
        string projectId,
        string contractId,
        DeploymentReviewRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.ReviewAsync(
                projectId,
                contractId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeploymentResponse>>> GetAllAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.GetAllAsync(projectId, contractId, cancellationToken)
        );

    [HttpPost("attempts")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> StartAttemptAsync(
        string projectId,
        string contractId,
        StartSessionDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.StartAttemptAsync(
                projectId,
                contractId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpPost("{deploymentId}/wallet-opened")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> MarkWalletOpenedAsync(
        string projectId,
        string contractId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.MarkWalletOpenedAsync(
                projectId,
                contractId,
                deploymentId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpPost("{deploymentId}/submitted")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> MarkSubmittedAsync(
        string projectId,
        string contractId,
        string deploymentId,
        SubmitDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.MarkSubmittedAsync(
                projectId,
                contractId,
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
        string contractId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.ConfirmAttemptAsync(
                projectId,
                contractId,
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
        string contractId,
        string deploymentId,
        FailDeploymentAttemptRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.MarkFailedAsync(
                projectId,
                contractId,
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
        string contractId,
        string deploymentId,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.ResumeAttemptAsync(
                projectId,
                contractId,
                deploymentId,
                currentWallet.Current!,
                cancellationToken
            )
        );

    [HttpPost("recover")]
    [RequireWalletSession]
    public async Task<ActionResult<DeploymentResponse>> RecoverAsync(
        string projectId,
        string contractId,
        RecoverDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        Respond(
            projectId,
            contractId,
            await lifecycle.RecoverAsync(
                projectId,
                contractId,
                request,
                currentWallet.Current!,
                cancellationToken
            )
        );

    private ActionResult Respond<T>(
        string projectId,
        string contractId,
        DeploymentWorkflowResult<T> result
    )
    {
        if (result.IsSuccess)
        {
            return
                result.StatusCode == StatusCodes.Status201Created
                && result.Value is DeploymentResponse deployment
                ? Created(
                    $"/api/projects/{projectId}/contracts/{contractId}/deployments/{deployment.Id}",
                    deployment
                )
                : Ok(result.Value);
        }

        object body = result.RelatedDeployment is null
            ? new { error = result.Error }
            : new { error = result.Error, deployment = result.RelatedDeployment };

        return StatusCode(result.StatusCode, body);
    }
}
