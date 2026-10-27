using backend.Models;

namespace backend.Repositories;

public interface IDeploymentRepository
{
    Task<IReadOnlyList<DeploymentDocument>> GetForScopeAsync(
        string projectId,
        string? contractId,
        CancellationToken cancellationToken
    );

    Task<DeploymentDocument?> MarkWalletOpenedAsync(
        DeploymentDocument attempt,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken
    );

    Task InsertAsync(DeploymentDocument deployment, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeploymentDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken
    );

    Task<DeploymentDocument?> GetByTransactionIdAsync(
        string transactionId,
        CancellationToken cancellationToken
    );

    Task<DeploymentDocument?> GetByIdAsync(
        string deploymentId,
        CancellationToken cancellationToken
    );

    Task ReplaceAsync(DeploymentDocument deployment, CancellationToken cancellationToken);

    Task<DeploymentDocument?> RenewCapabilityAsync(
        DeploymentDocument deployment,
        string capabilityHash,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken
    );
}
