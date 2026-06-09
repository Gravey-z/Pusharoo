using backend.Models;

namespace backend.Repositories;

public interface IDeploymentRepository
{
    Task InsertAsync(DeploymentDocument deployment, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeploymentDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken);
}
