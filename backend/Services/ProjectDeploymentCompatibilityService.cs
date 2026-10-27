using backend.Models;

namespace backend.Services;

public sealed class ProjectDeploymentCompatibilityService(
    LegacyContractResolver resolver,
    DeploymentLifecycleService legacy,
    DeploymentRecoveryService legacyRecovery,
    ContractDeploymentService scoped
)
{
    public Task<DeploymentWorkflowResult<IReadOnlyList<DeploymentResponse>>> GetAllAsync(
        string projectId,
        CancellationToken cancellationToken
    ) => legacy.GetAllAsync(projectId, cancellationToken);

    public async Task<DeploymentWorkflowResult<DeploymentReviewResponse>> ReviewAsync(
        string projectId,
        DeploymentReviewRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        var contractId = await resolver.ResolveOperationAsync(projectId, cancellationToken);
        return contractId is null
            ? await legacy.ReviewAsync(projectId, request, actor, cancellationToken)
            : await scoped.ReviewAsync(projectId, contractId, request, actor, cancellationToken);
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> StartAttemptAsync(
        string projectId,
        StartSessionDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        var contractId = await resolver.ResolveOperationAsync(projectId, cancellationToken);
        if (request.ContractId is not null && request.ContractId != contractId)
            throw new ContractOperationException(
                409,
                "contract_required",
                "The requested contract does not match the sole project contract."
            );
        return contractId is null
            ? await legacy.StartAttemptAsync(projectId, request, actor, cancellationToken)
            : await scoped.StartAttemptAsync(
                projectId,
                contractId,
                request with
                {
                    ContractId = contractId,
                },
                actor,
                cancellationToken
            );
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> RecoverAsync(
        string projectId,
        RecoverDeploymentRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        var contractId = await resolver.ResolveRecoveryAsync(
            projectId,
            request.TransactionId,
            cancellationToken
        );
        return contractId is null
            ? await legacyRecovery.RecoverAsync(projectId, request, actor, cancellationToken)
            : await scoped.RecoverAsync(projectId, contractId, request, actor, cancellationToken);
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> MarkWalletOpenedAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        var contractId = await resolver.ResolveAttemptAsync(
            projectId,
            deploymentId,
            cancellationToken
        );
        return contractId is null
            ? await legacy.MarkWalletOpenedAsync(
                projectId,
                deploymentId,
                request,
                actor,
                cancellationToken
            )
            : await scoped.MarkWalletOpenedAsync(
                projectId,
                contractId,
                deploymentId,
                request,
                actor,
                cancellationToken
            );
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> MarkSubmittedAsync(
        string projectId,
        string deploymentId,
        SubmitDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        var contractId = await resolver.ResolveAttemptAsync(
            projectId,
            deploymentId,
            cancellationToken
        );
        return contractId is null
            ? await legacy.MarkSubmittedAsync(
                projectId,
                deploymentId,
                request,
                actor,
                cancellationToken
            )
            : await scoped.MarkSubmittedAsync(
                projectId,
                contractId,
                deploymentId,
                request,
                actor,
                cancellationToken
            );
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> ConfirmAttemptAsync(
        string projectId,
        string deploymentId,
        ConfirmDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        var contractId = await resolver.ResolveAttemptAsync(
            projectId,
            deploymentId,
            cancellationToken
        );
        return contractId is null
            ? await legacy.ConfirmAttemptAsync(
                projectId,
                deploymentId,
                request,
                actor,
                cancellationToken
            )
            : await scoped.ConfirmAttemptAsync(
                projectId,
                contractId,
                deploymentId,
                request,
                actor,
                cancellationToken
            );
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> MarkFailedAsync(
        string projectId,
        string deploymentId,
        FailDeploymentAttemptRequest request,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        var contractId = await resolver.ResolveAttemptAsync(
            projectId,
            deploymentId,
            cancellationToken
        );
        return contractId is null
            ? await legacy.MarkFailedAsync(
                projectId,
                deploymentId,
                request,
                actor,
                cancellationToken
            )
            : await scoped.MarkFailedAsync(
                projectId,
                contractId,
                deploymentId,
                request,
                actor,
                cancellationToken
            );
    }

    public async Task<DeploymentWorkflowResult<DeploymentResponse>> ResumeAttemptAsync(
        string projectId,
        string deploymentId,
        WalletSessionIdentity actor,
        CancellationToken cancellationToken
    )
    {
        var contractId = await resolver.ResolveAttemptAsync(
            projectId,
            deploymentId,
            cancellationToken
        );
        return contractId is null
            ? await legacy.ResumeAttemptAsync(projectId, deploymentId, actor, cancellationToken)
            : await scoped.ResumeAttemptAsync(
                projectId,
                contractId,
                deploymentId,
                actor,
                cancellationToken
            );
    }
}
