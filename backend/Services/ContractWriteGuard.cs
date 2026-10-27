using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed class ContractWriteGuard(MongoDbContext db, IOptions<MultiContractOptions> options)
{
    public async Task<string> AcquireAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!options.Value.WritesEnabled)
            throw new ContractOperationException(
                503,
                "contracts_not_ready",
                "Contract management is not enabled."
            );

        var token = Guid.NewGuid().ToString("N");
        var result = await db.Projects.UpdateOneAsync(
            project =>
                project.Id == projectId
                && project.ContractSchemaVersion == 1
                && !project.IsDeleting
                && project.ContractWriteToken == null,
            Builders<ProjectDocument>.Update.Set(project => project.ContractWriteToken, token),
            cancellationToken: cancellationToken
        );

        if (result.MatchedCount == 0)
            throw new ContractOperationException(
                409,
                "project_not_ready",
                "The project requires migration, is busy, or is being deleted. Retry when it is ready."
            );

        try
        {
            await EnsureArtifactIndexesAsync(cancellationToken);
            var hasUnmappedArtifacts = await db
                .ContractArtifacts.Aggregate<BsonDocument>(
                    new BsonDocument[]
                    {
                        new("$match", new BsonDocument("projectId", projectId)),
                        new(
                            "$lookup",
                            new BsonDocument
                            {
                                { "from", db.Contracts.CollectionNamespace.CollectionName },
                                { "localField", "contractId" },
                                { "foreignField", "_id" },
                                { "as", "contractParent" },
                            }
                        ),
                        new(
                            "$match",
                            new BsonDocument(
                                "contractParent.projectId",
                                new BsonDocument("$ne", projectId)
                            )
                        ),
                        new("$limit", 1),
                        new("$project", new BsonDocument("_id", 1)),
                    }
                )
                .AnyAsync(cancellationToken);
            var hasDeployments = await db
                .Deployments.Find(deployment => deployment.ProjectId == projectId)
                .AnyAsync(cancellationToken);

            if (hasUnmappedArtifacts || hasDeployments)
                throw new ContractOperationException(
                    409,
                    "contracts_not_ready",
                    "Artifact mappings or deployment history require the remaining migration phases."
                );

            return token;
        }
        catch
        {
            await ReleaseAsync(projectId, token);
            throw;
        }
    }

    private async Task EnsureArtifactIndexesAsync(CancellationToken cancellationToken)
    {
        using var cursor = await db.ContractArtifacts.Indexes.ListAsync(cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        var scopedKey = new BsonDocument
        {
            { "projectId", 1 },
            { "contractId", 1 },
            { "version", 1 },
        };
        var hasScopedIndex = indexes.Any(index =>
            index["key"].AsBsonDocument.Equals(scopedKey)
            && index.GetValue("unique", false).ToBoolean()
            && !index.Contains("partialFilterExpression")
        );
        var hasLegacyIndex = indexes.Any(index =>
            index["key"].AsBsonDocument.ElementCount == 2
            && index["key"]
                .AsBsonDocument.Names.ToHashSet()
                .SetEquals(new[] { "projectId", "version" })
            && index.GetValue("unique", false).ToBoolean()
        );

        if (!hasScopedIndex || hasLegacyIndex)
            throw new ContractOperationException(
                503,
                "artifact_indexes_not_ready",
                "Contract artifact indexes require migration before writes can be enabled."
            );
    }

    public Task ReleaseAsync(string projectId, string token) =>
        db.Projects.UpdateOneAsync(
            project => project.Id == projectId && project.ContractWriteToken == token,
            Builders<ProjectDocument>.Update.Unset(project => project.ContractWriteToken),
            cancellationToken: CancellationToken.None
        );

    public async Task BeginDeletionAsync(string projectId, CancellationToken cancellationToken)
    {
        var result = await db.Projects.UpdateOneAsync(
            project => project.Id == projectId && project.ContractWriteToken == null,
            Builders<ProjectDocument>.Update.Set(project => project.IsDeleting, true),
            cancellationToken: cancellationToken
        );

        if (result.MatchedCount == 0)
            throw new ContractOperationException(
                409,
                "project_busy",
                "The project is busy. Retry deletion after contract management completes."
            );
    }
}
