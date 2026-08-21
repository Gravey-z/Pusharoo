using backend.Models;
using backend.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed class ProjectService(
    IProjectRepository projects,
    MongoDbContext db)
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

    public async Task<IReadOnlyList<ProjectListItemResponse>> GetListItemsAsync(CancellationToken cancellationToken)
    {
        var projectList = await projects.GetAllAsync(cancellationToken);
        if (projectList.Count == 0)
        {
            return [];
        }

        var projectIds = projectList.Select(project => project.Id).ToArray();
        var projectFilter = Builders<ArtifactDocument>.Filter.In(artifact => artifact.ProjectId, projectIds);
        var deploymentFilter = Builders<DeploymentDocument>.Filter.In(deployment => deployment.ProjectId, projectIds);
        var artifactsTask = db.ContractArtifacts
            .Find(projectFilter)
            .Project(artifact => new ProjectListArtifact(
                artifact.ProjectId,
                artifact.Version,
                artifact.CreatedAt))
            .ToListAsync(cancellationToken);
        var deploymentsTask = db.Deployments
            .Find(deploymentFilter)
            .Project(deployment => new ProjectListDeployment(
                deployment.ProjectId,
                deployment.Network,
                deployment.ContractHash,
                deployment.Status,
                deployment.CreatedAt))
            .ToListAsync(cancellationToken);

        await Task.WhenAll(artifactsTask, deploymentsTask);
        var artifactsByProject = artifactsTask.Result
            .GroupBy(artifact => artifact.ProjectId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(artifact => artifact.CreatedAt).First());
        var deploymentsByProject = deploymentsTask.Result
            .GroupBy(deployment => deployment.ProjectId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(deployment => deployment.CreatedAt).ToArray());

        return projectList.Select(project =>
        {
            artifactsByProject.TryGetValue(project.Id, out var latestArtifact);
            deploymentsByProject.TryGetValue(project.Id, out var projectDeployments);
            projectDeployments ??= [];
            var deploymentNetworks = projectDeployments
                .Select(deployment => deployment.Network)
                .Distinct()
                .ToArray();
            var deployed = projectDeployments.Any(deployment =>
                !string.IsNullOrWhiteSpace(deployment.ContractHash)
                && (string.IsNullOrWhiteSpace(deployment.Status) || deployment.Status == "confirmed"));

            return new ProjectListItemResponse(
                project.ToResponse(),
                latestArtifact is null ? null : new ProjectListArtifactResponse(latestArtifact.Version, latestArtifact.CreatedAt),
                deploymentNetworks,
                deployed);
        }).ToArray();
    }

    public async Task<ProjectDocument?> GetByIdAsync(string projectId, CancellationToken cancellationToken)
    {
        return await projects.GetByIdAsync(projectId, cancellationToken);
    }

    public async Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        var subscriptions = await db.WebhookSubscriptions
            .Find(Builders<BsonDocument>.Filter.Eq("projectId", projectId))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(cancellationToken);
        var subscriptionIds = subscriptions
            .Where(subscription => subscription.TryGetValue("_id", out _))
            .Select(subscription => subscription["_id"].ToString())
            .ToArray();

        if (subscriptionIds.Length > 0)
        {
            var deliveries = await db.WebhookDeliveries
                .Find(Builders<BsonDocument>.Filter.In("subscriptionId", subscriptionIds))
                .Project(Builders<BsonDocument>.Projection.Include("_id"))
                .ToListAsync(cancellationToken);
            var deliveryIds = deliveries
                .Where(delivery => delivery.TryGetValue("_id", out _))
                .Select(delivery => delivery["_id"].ToString())
                .ToArray();

            if (deliveryIds.Length > 0)
            {
                await db.WebhookDeliveryAttempts.DeleteManyAsync(
                    Builders<BsonDocument>.Filter.In("deliveryId", deliveryIds),
                    cancellationToken);
            }

            await db.WebhookDeliveries.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In("subscriptionId", subscriptionIds),
                cancellationToken);
        }

        await db.WebhookSubscriptions.DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("projectId", projectId),
            cancellationToken);
        await db.RelayEntitlements.DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("projectId", projectId),
            cancellationToken);
        await db.RelayPaymentIntents.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("projectId", projectId), cancellationToken);
        await db.RelayPayments.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("projectId", projectId), cancellationToken);
        await db.RelayEntitlementHistory.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("projectId", projectId), cancellationToken);
        await db.Deployments.DeleteManyAsync(deployment => deployment.ProjectId == projectId, cancellationToken);
        await db.ContractArtifacts.DeleteManyAsync(artifact => artifact.ProjectId == projectId, cancellationToken);
        await db.Projects.DeleteOneAsync(project => project.Id == projectId, cancellationToken);
    }

    private sealed record ProjectListArtifact(string ProjectId, string Version, DateTime CreatedAt);

    private sealed record ProjectListDeployment(
        string ProjectId,
        string Network,
        string? ContractHash,
        string Status,
        DateTime CreatedAt);
}
