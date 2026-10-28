using backend.Models;
using backend.Repositories;

namespace backend.Services;

public sealed class LegacyContractResolver(
    ProjectContractLoader loader,
    IContractRepository contracts,
    DeploymentService deployments,
    ContractWriteGuard migrationWrites
)
{
    public async Task<string?> ResolveOperationAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        await migrationWrites.EnsureMigrationAllowsWritesAsync(cancellationToken);
        var project = await loader.LoadProjectAsync(projectId, cancellationToken);
        var children = await contracts.ListAsync(project.Id, 0, 2, cancellationToken);
        if (children.Count == 1)
            return children[0].Id;
        if (children.Count == 0 && CanUseUnmappedLegacy(project))
            return null;
        throw new ContractOperationException(
            409,
            "contract_required",
            children.Count == 0
                ? "Add a contract before continuing."
                : "Choose a contract before continuing."
        );
    }

    public async Task<string?> ResolveAttemptAsync(
        string projectId,
        string deploymentId,
        CancellationToken cancellationToken
    )
    {
        await migrationWrites.EnsureMigrationAllowsWritesAsync(
            cancellationToken,
            allowReconciliation: true
        );
        var project = await loader.LoadProjectAsync(projectId, cancellationToken);
        var attempt = await deployments.GetByIdAsync(deploymentId, cancellationToken);
        if (attempt is null || attempt.ProjectId != project.Id)
            throw new ContractOperationException(
                404,
                "deployment_not_found",
                "Deployment attempt was not found."
            );
        if (attempt.ContractId is not null)
        {
            await loader.LoadAsync(project.Id, attempt.ContractId, cancellationToken);
            return attempt.ContractId;
        }
        if (CanUseUnmappedLegacy(project))
            return null;
        throw new ContractOperationException(
            409,
            "legacy_attempt_requires_reconciliation",
            "This deployment attempt requires its original contract mapping before it can continue."
        );
    }

    public async Task<string?> ResolveRecoveryAsync(
        string projectId,
        string transactionId,
        CancellationToken cancellationToken
    )
    {
        await loader.LoadProjectAsync(projectId, cancellationToken);
        var recorded = await deployments.GetByTransactionIdAsync(transactionId, cancellationToken);
        if (recorded is not null)
        {
            if (recorded.ProjectId != projectId)
                throw new ContractOperationException(
                    409,
                    "transaction_conflict",
                    "This transaction is already recorded for another project."
                );
            return await ResolveAttemptAsync(projectId, recorded.Id, cancellationToken);
        }
        return await ResolveOperationAsync(projectId, cancellationToken);
    }

    private static bool CanUseUnmappedLegacy(ProjectDocument project) =>
        project.ContractSchemaVersion == 0;
}
