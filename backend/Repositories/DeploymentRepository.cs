using backend.Models;
using backend.Services;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class DeploymentRepository(MongoDbContext db) : IDeploymentRepository
{
    public async Task InsertAsync(DeploymentDocument deployment, CancellationToken cancellationToken)
    {
        await db.Deployments.InsertOneAsync(deployment, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<DeploymentDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        return await db.Deployments
            .Find(deployment => deployment.ProjectId == projectId)
            .SortByDescending(deployment => deployment.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}
