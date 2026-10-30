using backend.Models;
using backend.Repositories;

namespace backend.Services;

public sealed class WorkspaceReadService(
    ProjectContractLoader loader,
    WorkspaceReadRepository workspace,
    IDeploymentRepository deployments,
    ProjectRetentionService retention
)
{
    public async Task<ProjectWorkspaceResponse> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var project = await loader.LoadProjectAsync(projectId, cancellationToken);
        var contractTask = workspace.GetContractsAsync(project.Id, cancellationToken);
        var artifactTask = workspace.GetArtifactSummariesAsync(project.Id, cancellationToken);
        var deploymentTask = workspace.GetProjectDeploymentsAsync(project.Id, cancellationToken);
        await Task.WhenAll(contractTask, artifactTask, deploymentTask);
        var contracts = await contractTask;
        var artifacts = await artifactTask;
        var history = await deploymentTask;
        var artifactsByContract = artifacts.ToLookup(item => item.ContractId ?? "");
        var deploymentsByContract = history.ToLookup(item => item.ContractId ?? "");
        var summaries = contracts
            .Select(contract => new ContractWorkspaceSummary(
                contract.ToResponse(),
                artifactsByContract[contract.Id].Count(),
                deploymentsByContract[contract.Id].Count(),
                artifactsByContract[contract.Id].FirstOrDefault(),
                Confirmed(deploymentsByContract[contract.Id])
                    .GroupBy(item => item.Network)
                    .Select(group => group.First().ToResponse())
                    .ToArray()
            ))
            .ToArray();

        return new(
            await retention.ToResponseAsync(project, cancellationToken),
            summaries,
            artifacts.Count,
            history.Count,
            history.Take(20).Select(item => item.ToResponse()).ToArray()
        );
    }

    public async Task<ContractOverviewResponse> GetContractAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    )
    {
        var context = await loader.LoadAsync(projectId, contractId, cancellationToken);
        var artifactTask = workspace.GetContractArtifactsAsync(
            projectId,
            contractId,
            cancellationToken
        );
        var deploymentTask = deployments.GetForScopeAsync(projectId, contractId, cancellationToken);
        await Task.WhenAll(artifactTask, deploymentTask);
        var artifacts = (await artifactTask).Select(item => item.ToResponse()).ToArray();
        var history = await deploymentTask;
        var latest = Confirmed(history).FirstOrDefault()?.ToResponse();
        return new(
            await retention.ToResponseAsync(context.Project, cancellationToken),
            context.Contract.ToResponse(),
            artifacts,
            artifacts.FirstOrDefault(),
            history.Select(item => item.ToResponse()).ToArray(),
            latest,
            latest is not null
        );
    }

    private static IEnumerable<DeploymentDocument> Confirmed(
        IEnumerable<DeploymentDocument> history
    ) =>
        history
            .Where(item =>
                DeploymentRecoveryPolicy.IsConfirmed(item)
                && !string.IsNullOrWhiteSpace(item.ContractHash)
            )
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id);
}
