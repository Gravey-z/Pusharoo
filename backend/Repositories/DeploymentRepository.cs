using System.Text.RegularExpressions;
using backend.Models;
using backend.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class DeploymentRepository(MongoDbContext db) : IDeploymentRepository
{
    public async Task<IReadOnlyList<DeploymentDocument>> GetForScopeAsync(
        string projectId,
        string? contractId,
        CancellationToken cancellationToken
    ) =>
        await db
            .Deployments.Find(deployment =>
                deployment.ProjectId == projectId && deployment.ContractId == contractId
            )
            .SortByDescending(deployment => deployment.CreatedAt)
            .ThenByDescending(deployment => deployment.Id)
            .ToListAsync(cancellationToken);

    public async Task<DeploymentDocument?> MarkWalletOpenedAsync(
        DeploymentDocument attempt,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken
    ) =>
        await db.Deployments.FindOneAndUpdateAsync(
            item =>
                item.Id == attempt.Id
                && item.ProjectId == attempt.ProjectId
                && item.ContractId == attempt.ContractId
                && item.Status == "awaiting_wallet"
                && item.BroadcastTrackingVersion == 1
                && !item.BroadcastOutcomeUnknown
                && item.TransactionId == null
                && item.AttemptCapabilityHash == attempt.AttemptCapabilityHash,
            Builders<DeploymentDocument>
                .Update.Set(item => item.BroadcastOutcomeUnknown, true)
                .Set(item => item.UpdatedAt, updatedAtUtc),
            new FindOneAndUpdateOptions<DeploymentDocument>
            {
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken
        );

    public async Task InsertAsync(
        DeploymentDocument deployment,
        CancellationToken cancellationToken
    )
    {
        await db.Deployments.InsertOneAsync(deployment, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<DeploymentDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        return await db
            .Deployments.Find(deployment => deployment.ProjectId == projectId)
            .SortByDescending(deployment => deployment.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<DeploymentDocument?> GetByTransactionIdAsync(
        string transactionId,
        CancellationToken cancellationToken
    )
    {
        var hash = transactionId.Trim();

        if (hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hash = hash[2..];

        var exactHash = new BsonRegularExpression($"^(?:0x)?{Regex.Escape(hash)}$", "i");

        return await db
            .Deployments.Find(
                Builders<DeploymentDocument>.Filter.Regex(
                    deployment => deployment.TransactionId,
                    exactHash
                )
            )
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<DeploymentDocument?> GetByIdAsync(
        string deploymentId,
        CancellationToken cancellationToken
    )
    {
        return await db
            .Deployments.Find(deployment => deployment.Id == deploymentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task ReplaceAsync(
        DeploymentDocument deployment,
        CancellationToken cancellationToken
    )
    {
        var filter = Builders<DeploymentDocument>.Filter.Where(item =>
            item.Id == deployment.Id
            && item.ProjectId == deployment.ProjectId
            && item.ContractId == deployment.ContractId
        );
        if (deployment.TransactionId is null)
            filter &= Builders<DeploymentDocument>.Filter.Eq(item => item.TransactionId, null);
        else
        {
            var hash = deployment.TransactionId.Trim();
            if (hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hash = hash[2..];
            var sameTransaction = new BsonRegularExpression($"^(?:0x)?{Regex.Escape(hash)}$", "i");
            filter &=
                Builders<DeploymentDocument>.Filter.Eq(item => item.TransactionId, null)
                | Builders<DeploymentDocument>.Filter.Regex(
                    item => item.TransactionId,
                    sameTransaction
                );
        }
        if (!DeploymentRecoveryPolicy.IsConfirmed(deployment))
            filter &=
                Builders<DeploymentDocument>.Filter.Ne(item => item.Status, "confirmed")
                & Builders<DeploymentDocument>.Filter.Not(
                    Builders<DeploymentDocument>.Filter.Regex(
                        item => item.Status,
                        new BsonRegularExpression(@"^\s*$")
                    )
                );

        if (
            deployment.Status == "failed"
            && deployment.TransactionId is null
            && !deployment.BroadcastOutcomeUnknown
        )
            filter &= Builders<DeploymentDocument>.Filter.Where(item =>
                !item.BroadcastOutcomeUnknown
                && item.TransactionId == null
                && item.AttemptCapabilityHash == deployment.AttemptCapabilityHash
                && (item.Status == "awaiting_wallet" || item.Status == "preparing")
            );
        var result = await db.Deployments.ReplaceOneAsync(
            filter,
            deployment,
            cancellationToken: cancellationToken
        );
        if (result.MatchedCount == 0)
            throw new ContractOperationException(
                409,
                "deployment_changed",
                "The deployment was removed or its parent changed. Refresh and try again."
            );
    }

    public async Task<DeploymentDocument?> RenewCapabilityAsync(
        DeploymentDocument deployment,
        string capabilityHash,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken
    )
    {
        var filter = Builders<DeploymentDocument>.Filter.Where(item =>
            item.Id == deployment.Id
            && item.ProjectId == deployment.ProjectId
            && item.ContractId == deployment.ContractId
            && item.AttemptCapabilityHash == deployment.AttemptCapabilityHash
            && item.Status == deployment.Status
        );
        var update = Builders<DeploymentDocument>
            .Update.Set(item => item.AttemptCapabilityHash, capabilityHash)
            .Set(item => item.AttemptCapabilityExpiresAtUtc, expiresAtUtc);

        return await db.Deployments.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<DeploymentDocument>
            {
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken
        );
    }
}
