using backend.Models;
using backend.Services;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class ArtifactRepository(MongoDbContext db) : IArtifactRepository
{
    public async Task InsertAsync(ArtifactDocument artifact, CancellationToken cancellationToken)
    {
        await db.ContractArtifacts.InsertOneAsync(artifact, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        return await db.ContractArtifacts
            .Find(artifact => artifact.ProjectId == projectId)
            .SortByDescending(artifact => artifact.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ArtifactDocument?> GetByIdAsync(string artifactId, CancellationToken cancellationToken)
    {
        return await db.ContractArtifacts
            .Find(artifact => artifact.Id == artifactId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
