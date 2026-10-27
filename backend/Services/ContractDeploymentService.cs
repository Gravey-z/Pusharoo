using backend.Models;

namespace backend.Services;

public sealed class ContractDeploymentService(
    DeploymentLifecycleService lifecycle,
    DeploymentService deployments,
    ProjectContractLoader loader,
    ContractDeploymentCoordinator coordinator
)
{
    public async Task<DeploymentWorkflowResult<DeploymentReviewResponse>> ReviewAsync(
        string projectId,
        string contractId,
        DeploymentReviewRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        await coordinator.EnsureReadyAsync(projectId, contractId, cancellationToken);
        return await lifecycle.ReviewAsync(
            projectId,
            request,
            actor,
            cancellationToken,
            contractId
        );
    }

    public async Task<DeploymentWorkflowResult<IReadOnlyList<DeploymentResponse>>> GetAllAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    )
    {
        await loader.LoadAsync(projectId, contractId, cancellationToken);
        return await lifecycle.GetAllAsync(projectId, cancellationToken, contractId);
    }

    public Task<DeploymentWorkflowResult<DeploymentResponse>> StartAttemptAsync(
        string projectId,
        string contractId,
        StartSessionDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    ) =>
        coordinator.ExecuteAsync(
            projectId,
            contractId,
            request.Network,
            () =>
                lifecycle.StartAttemptAsync(
                    projectId,
                    request,
                    actor,
                    cancellationToken,
                    contractId
                ),
            cancellationToken
        );

    public Task<DeploymentWorkflowResult<DeploymentResponse>> MarkSubmittedAsync(
        string projectId,
        string contractId,
        string deploymentId,
        SubmitDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    ) =>
        ExecuteAttemptAsync(
            projectId,
            contractId,
            deploymentId,
            () =>
                lifecycle.MarkSubmittedAsync(
                    projectId,
                    deploymentId,
                    request,
                    actor,
                    cancellationToken,
                    contractId
                ),
            cancellationToken
        );

    public Task<DeploymentWorkflowResult<DeploymentResponse>> ConfirmAttemptAsync(
        string projectId,
        string contractId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    ) =>
        ExecuteAttemptAsync(
            projectId,
            contractId,
            deploymentId,
            () =>
                lifecycle.ConfirmAttemptAsync(
                    projectId,
                    deploymentId,
                    request,
                    actor,
                    cancellationToken,
                    contractId
                ),
            cancellationToken
        );

    public Task<DeploymentWorkflowResult<DeploymentResponse>> MarkFailedAsync(
        string projectId,
        string contractId,
        string deploymentId,
        FailDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    ) =>
        ExecuteAttemptAsync(
            projectId,
            contractId,
            deploymentId,
            () =>
                lifecycle.MarkFailedAsync(
                    projectId,
                    deploymentId,
                    request,
                    actor,
                    cancellationToken,
                    contractId
                ),
            cancellationToken
        );

    public Task<DeploymentWorkflowResult<DeploymentResponse>> ResumeAttemptAsync(
        string projectId,
        string contractId,
        string deploymentId,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    ) =>
        ExecuteAttemptAsync(
            projectId,
            contractId,
            deploymentId,
            () =>
                lifecycle.ResumeAttemptAsync(
                    projectId,
                    deploymentId,
                    actor,
                    cancellationToken,
                    contractId
                ),
            cancellationToken
        );

    private async Task<DeploymentWorkflowResult<DeploymentResponse>> ExecuteAttemptAsync(
        string projectId,
        string contractId,
        string deploymentId,
        Func<Task<DeploymentWorkflowResult<DeploymentResponse>>> action,
        CancellationToken cancellationToken
    )
    {
        await loader.LoadAsync(projectId, contractId, cancellationToken);
        var attempt = await deployments.GetByIdAsync(deploymentId, cancellationToken);
        if (attempt is null || attempt.ProjectId != projectId || attempt.ContractId != contractId)
            return DeploymentWorkflowResult<DeploymentResponse>.NotFound(
                "Deployment attempt was not found."
            );

        return await coordinator.ExecuteAsync(
            projectId,
            contractId,
            attempt.Network,
            action,
            cancellationToken
        );
    }
}
