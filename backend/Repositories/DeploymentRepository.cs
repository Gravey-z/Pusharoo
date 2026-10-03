using backend.Models;
using backend.Services;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;

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

    public async Task<DeploymentDocument?> GetByTransactionIdAsync(string transactionId, CancellationToken cancellationToken)
    {
        var hash = transactionId.Trim();
        if (hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hash = hash[2..];
        var exactHash = new BsonRegularExpression($"^(?:0x)?{Regex.Escape(hash)}$", "i");
        return await db.Deployments.Find(Builders<DeploymentDocument>.Filter.Regex(deployment => deployment.TransactionId, exactHash))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<DeploymentDocument?> GetByIdAsync(string deploymentId, CancellationToken cancellationToken)
    {
        return await db.Deployments.Find(deployment => deployment.Id == deploymentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task ReplaceAsync(DeploymentDocument deployment, CancellationToken cancellationToken)
    {
        await db.Deployments.ReplaceOneAsync(
            item => item.Id == deployment.Id,
            deployment,
            cancellationToken: cancellationToken);
    }
}
