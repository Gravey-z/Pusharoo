using backend.Models;

namespace backend.Repositories;

public interface IArtifactRepository
{
    Task<bool> HasContractArtifactsAsync(string projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactListItemResponse>> GetHistoryAsync(
        string projectId,
        string? contractId,
        int skip,
        int take,
        CancellationToken cancellationToken
    );

    Task<ArtifactDocument?> GetByContractAndVersionAsync(
        string projectId,
        string contractId,
        string version,
        CancellationToken cancellationToken
    );

    Task InsertAsync(ArtifactDocument artifact, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken
    );

    Task<ArtifactDocument?> GetByProjectIdAndVersionAsync(
        string projectId,
        string version,
        CancellationToken cancellationToken
    );

    Task<ArtifactDocument?> GetByIdAsync(string artifactId, CancellationToken cancellationToken);

    Task<ArtifactDocument?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken
    );
}
