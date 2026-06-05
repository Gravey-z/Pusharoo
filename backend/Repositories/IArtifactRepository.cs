using backend.Models;

namespace backend.Repositories;

public interface IArtifactRepository
{
    Task InsertAsync(ArtifactDocument artifact, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactDocument>> GetByProjectIdAsync(string projectId, CancellationToken cancellationToken);

    Task<ArtifactDocument?> GetByProjectIdAndVersionAsync(
        string projectId,
        string version,
        CancellationToken cancellationToken);

    Task<ArtifactDocument?> GetByIdAsync(string artifactId, CancellationToken cancellationToken);
}
