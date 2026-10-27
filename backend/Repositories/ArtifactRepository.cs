using backend.Models;
using backend.Services;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class ArtifactRepository(MongoDbContext db) : IArtifactRepository
{
    public Task<bool> HasContractArtifactsAsync(
        string projectId,
        CancellationToken cancellationToken
    ) =>
        db
            .ContractArtifacts.Find(artifact =>
                artifact.ProjectId == projectId && artifact.ContractId != null
            )
            .AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<ArtifactListItemResponse>> GetHistoryAsync(
        string projectId,
        string? contractId,
        int skip,
        int take,
        CancellationToken cancellationToken
    )
    {
        var filter = Builders<ArtifactDocument>.Filter.Eq(
            artifact => artifact.ProjectId,
            projectId
        );
        if (contractId is not null)
            filter &= Builders<ArtifactDocument>.Filter.Eq(
                artifact => artifact.ContractId,
                contractId
            );

        return await db
            .ContractArtifacts.Find(filter)
            .SortByDescending(artifact => artifact.CreatedAt)
            .ThenByDescending(artifact => artifact.Id)
            .Skip(skip)
            .Limit(take)
            .Project(artifact => new ArtifactListItemResponse(
                artifact.Id,
                artifact.ProjectId,
                artifact.ContractId,
                artifact.Version,
                artifact.Notes,
                artifact.ContractName,
                artifact.NefFileName,
                artifact.NefSize,
                artifact.Summary,
                artifact.Warnings,
                artifact.CreatedAt
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<ArtifactDocument?> GetByContractAndVersionAsync(
        string projectId,
        string contractId,
        string version,
        CancellationToken cancellationToken
    ) =>
        await db
            .ContractArtifacts.Find(artifact =>
                artifact.ProjectId == projectId
                && artifact.ContractId == contractId
                && artifact.Version == version
            )
            .Project<ArtifactDocument>(
                Builders<ArtifactDocument>.Projection.Exclude(artifact => artifact.Nef)
            )
            .FirstOrDefaultAsync(cancellationToken);

    public async Task InsertAsync(ArtifactDocument artifact, CancellationToken cancellationToken)
    {
        await db.ContractArtifacts.InsertOneAsync(artifact, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        return await db
            .ContractArtifacts.Find(artifact => artifact.ProjectId == projectId)
            .SortByDescending(artifact => artifact.CreatedAt)
            .ThenByDescending(artifact => artifact.Id)
            .Project<ArtifactDocument>(
                Builders<ArtifactDocument>.Projection.Exclude(artifact => artifact.Nef)
            )
            .ToListAsync(cancellationToken);
    }

    public async Task<ArtifactDocument?> GetByProjectIdAndVersionAsync(
        string projectId,
        string version,
        CancellationToken cancellationToken
    )
    {
        return await db
            .ContractArtifacts.Find(artifact =>
                artifact.ProjectId == projectId
                && artifact.ContractId == null
                && artifact.Version == version
            )
            .SortByDescending(artifact => artifact.CreatedAt)
            .Project<ArtifactDocument>(
                Builders<ArtifactDocument>.Projection.Exclude(artifact => artifact.Nef)
            )
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ArtifactDocument?> GetByIdAsync(
        string artifactId,
        CancellationToken cancellationToken
    )
    {
        return await db
            .ContractArtifacts.Find(artifact => artifact.Id == artifactId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ArtifactDocument?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        return await db
            .ContractArtifacts.Find(artifact => artifact.IdempotencyKey == idempotencyKey)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
