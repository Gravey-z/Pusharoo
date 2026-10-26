using backend.Models;
using backend.Repositories;

namespace backend.Services;

public sealed class ProjectContractLoader(
    IProjectRepository projects,
    IContractRepository contracts
)
{
    public async Task<ProjectDocument> LoadProjectAsync(
        string projectId,
        CancellationToken cancellationToken
    ) =>
        await projects.GetByIdAsync(projectId, cancellationToken)
        ?? throw new ContractOperationException(404, "project_not_found", "Project was not found.");

    public async Task<ProjectContractContext> LoadAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    )
    {
        var project = await LoadProjectAsync(projectId, cancellationToken);
        var contract =
            await contracts.GetAsync(project.Id, contractId, cancellationToken)
            ?? throw new ContractOperationException(
                404,
                "contract_not_found",
                "Contract was not found."
            );

        return new(project, contract);
    }
}

public sealed record ProjectContractContext(ProjectDocument Project, ContractDocument Contract);
