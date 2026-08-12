using backend.Models;
using backend.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed class ProjectService(IProjectRepository projects, MongoDbContext db)
{
    public async Task<ProjectDocument> CreateAsync(CreateProjectRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var signature = request.Signature
            ?? throw new InvalidOperationException("Project creation requires a wallet signature.");

        var project = new ProjectDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CreatedByWalletAddress = signature.Address.Trim(),
            CreatedByWalletScriptHash = signature.ScriptHash.Trim(),
            CreatedByWalletPublicKey = signature.PublicKey.Trim(),
            CreatorNetwork = signature.Network.Trim(),
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTime.UtcNow
        };

        await projects.InsertAsync(project, cancellationToken);

        return project;
    }

    public Task<ProjectDocument?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
        => projects.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

    public async Task<IReadOnlyList<ProjectDocument>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await projects.GetAllAsync(cancellationToken);
    }

    public async Task<ProjectDocument?> GetByIdAsync(string projectId, CancellationToken cancellationToken)
    {
        return await projects.GetByIdAsync(projectId, cancellationToken);
    }

    public async Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        var subscriptionFilter = Builders<BsonDocument>.Filter.Eq("projectId", projectId);
        var subscriptions = await db.WebhookSubscriptions
            .Find(subscriptionFilter)
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(cancellationToken);
        var subscriptionIds = subscriptions
            .Where(subscription => subscription.Contains("_id"))
            .Select(subscription => subscription["_id"].ToString())
            .ToArray();

        if (subscriptionIds.Length > 0)
        {
            await db.WebhookDeliveries.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In("subscriptionId", subscriptionIds),
                cancellationToken);
        }

        await db.WebhookSubscriptions.DeleteManyAsync(subscriptionFilter, cancellationToken);
        await db.Deployments.DeleteManyAsync(deployment => deployment.ProjectId == projectId, cancellationToken);
        await db.ContractArtifacts.DeleteManyAsync(artifact => artifact.ProjectId == projectId, cancellationToken);
        await db.Projects.DeleteOneAsync(project => project.Id == projectId, cancellationToken);
    }
}
