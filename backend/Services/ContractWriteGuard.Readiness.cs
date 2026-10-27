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
        return await db
            .Deployments.Aggregate<BsonDocument>(
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
                        "$lookup",
                        new BsonDocument
                        {
                            { "from", db.ContractArtifacts.CollectionNamespace.CollectionName },
                            { "localField", "artifactId" },
                            { "foreignField", "_id" },
                            { "as", "artifactParent" },
                        }
                    ),
                    new(
                        "$match",
                        new BsonDocument(
                            "$or",
                            new BsonArray
                            {
                                new BsonDocument(
                                    "contractParent.projectId",
                                    new BsonDocument("$ne", projectId)
                                ),
                                new BsonDocument(
                                    "artifactParent.projectId",
                                    new BsonDocument("$ne", projectId)
                                ),
                                new BsonDocument(
                                    "authorizationSnapshot.authorizationSchemaVersion",
                                    new BsonDocument("$ne", 4)
                                ),
                                new BsonDocument(
                                    "authorizationSnapshot.projectId",
                                    new BsonDocument("$ne", projectId)
                                ),
                                new BsonDocument(
                                    "$expr",
                                    new BsonDocument(
                                        "$or",
                                        new BsonArray
                                        {
                                            new BsonDocument(
                                                "$ne",
                                                new BsonArray
                                                {
                                                    new BsonDocument(
                                                        "$ifNull",
                                                        new BsonArray
                                                        {
                                                            "$authorizationSnapshot.contractId",
                                                            BsonNull.Value,
                                                        }
                                                    ),
                                                    "$contractId",
                                                }
                                            ),
                                            new BsonDocument(
                                                "$ne",
                                                new BsonArray
                                                {
                                                    new BsonDocument(
                                                        "$ifNull",
                                                        new BsonArray
                                                        {
                                                            "$authorizationSnapshot.artifactId",
                                                            BsonNull.Value,
                                                        }
                                                    ),
                                                    "$artifactId",
                                                }
                                            ),
                                            new BsonDocument(
                                                "$ne",
                                                new BsonArray
                                                {
                                                    new BsonDocument(
                                                        "$ifNull",
                                                        new BsonArray
                                                        {
                                                            "$authorizationSnapshot.network",
                                                            BsonNull.Value,
                                                        }
                                                    ),
                                                    "$network",
                                                }
                                            ),
                                            new BsonDocument(
                                                "$ne",
                                                new BsonArray
                                                {
                                                    new BsonDocument(
                                                        "$ifNull",
                                                        new BsonArray
                                                        {
                                                            "$authorizationSnapshot.operation",
                                                            BsonNull.Value,
                                                        }
                                                    ),
                                                    "$operation",
                                                }
                                            ),
                                        }
                                    )
                                ),
                                new BsonDocument(
                                    "$expr",
                                    new BsonDocument(
                                        "$ne",
                                        new BsonArray
                                        {
                                            new BsonDocument(
                                                "$arrayElemAt",
                                                new BsonArray { "$artifactParent.contractId", 0 }
                                            ),
                                            "$contractId",
                                        }
                                    )
                                ),
                                new BsonDocument(
                                    "$expr",
                                    new BsonDocument(
                                        "$and",
                                        new BsonArray
                                        {
                                            new BsonDocument(
                                                "$ne",
                                                new BsonArray
                                                {
                                                    new BsonDocument(
                                                        "$ifNull",
                                                        new BsonArray
                                                        {
                                                            "$activeAttemptKey",
                                                            BsonNull.Value,
                                                        }
                                                    ),
                                                    BsonNull.Value,
                                                }
                                            ),
                                            new BsonDocument(
                                                "$ne",
                                                new BsonArray
                                                {
                                                    "$activeAttemptKey",
                                                    new BsonDocument(
                                                        "$concat",
                                                        new BsonArray
                                                        {
                                                            "$projectId",
                                                            ":",
                                                            "$contractId",
                                                            ":",
                                                            "$network",
                                                        }
                                                    ),
                                                }
                                            ),
                                        }
                                    )
                                ),
                            }
                        )
                    ),
                    new("$limit", 1),
                    new("$project", new BsonDocument("_id", 1)),
                }
            )
            .AnyAsync(cancellationToken);
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
