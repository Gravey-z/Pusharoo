using backend.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed partial class ContractWriteGuard
{
    private async Task EnsureReadinessAsync(string projectId, CancellationToken cancellationToken)
    {
        await EnsureArtifactIndexesAsync(cancellationToken);
        await EnsureDeploymentIndexesAsync(cancellationToken);
        if (
            await HasUnmappedArtifactsAsync(projectId, cancellationToken)
            || await HasUnmappedDeploymentsAsync(projectId, cancellationToken)
        )
            throw new ContractOperationException(
                409,
                "contracts_not_ready",
                "Artifact or deployment mappings require the remaining migration phases."
            );
    }

    private async Task<bool> HasUnmappedArtifactsAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        return await db
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
    }

    private async Task<bool> HasUnmappedDeploymentsAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var contracts = await db
            .Contracts.Find(contract => contract.ProjectId == projectId)
            .Project(contract => contract.Id)
            .ToListAsync(cancellationToken);
        var artifacts = await db
            .ContractArtifacts.Find(artifact => artifact.ProjectId == projectId)
            .Project(artifact => new { artifact.Id, artifact.ContractId })
            .ToListAsync(cancellationToken);
        var projection = Builders<DeploymentDocument>
            .Projection.Include(attempt => attempt.ProjectId)
            .Include(attempt => attempt.ContractId)
            .Include(attempt => attempt.ArtifactId)
            .Include(attempt => attempt.Network)
            .Include(attempt => attempt.Operation)
            .Include(attempt => attempt.Status)
            .Include(attempt => attempt.ContractHash)
            .Include(attempt => attempt.TransactionId)
            .Include(attempt => attempt.DeployedBy)
            .Include(attempt => attempt.ActiveAttemptKey)
            .Include(attempt => attempt.LegacyContractMappingVerified)
            .Include(attempt => attempt.RecoveryEvidence)
            .Include("authorizationSnapshot.authorizationSchemaVersion")
            .Include("authorizationSnapshot.projectId")
            .Include("authorizationSnapshot.contractId")
            .Include("authorizationSnapshot.artifactId")
            .Include("authorizationSnapshot.network")
            .Include("authorizationSnapshot.operation");
        var deployments = await db
            .Deployments.Find(attempt => attempt.ProjectId == projectId)
            .Project<DeploymentDocument>(projection)
            .ToListAsync(cancellationToken);
        var contractIds = contracts.ToHashSet(StringComparer.Ordinal);
        var artifactParents = artifacts.ToDictionary(
            artifact => artifact.Id,
            artifact => artifact.ContractId,
            StringComparer.Ordinal
        );
        return deployments.Any(attempt =>
            !DeploymentRecoveryPolicy.HasMappedHistory(
                attempt,
                projectId,
                contractIds,
                artifactParents
            )
        );
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

    private async Task EnsureDeploymentIndexesAsync(CancellationToken cancellationToken)
    {
        using var cursor = await db.Deployments.Indexes.ListAsync(cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        foreach (var field in new[] { "activeAttemptKey", "transactionId" })
        {
            if (
                !indexes.Any(index =>
                    index["key"].AsBsonDocument.Equals(new BsonDocument(field, 1))
                    && index.GetValue("unique", false).ToBoolean()
                    && index.GetValue("sparse", false).ToBoolean()
                    && !index.Contains("partialFilterExpression")
                )
            )
                throw new ContractOperationException(
                    503,
                    "deployment_indexes_not_ready",
                    "Deployment indexes require migration before writes can be enabled."
                );
        }
    }
}
