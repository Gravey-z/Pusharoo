using backend.Models;

namespace backend.Repositories;

public interface IProjectRepository
{
    Task InsertAsync(ProjectDocument project, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectDocument>> GetAllAsync(CancellationToken cancellationToken);

    Task<ProjectDocument?> GetByIdAsync(string projectId, CancellationToken cancellationToken);
}
