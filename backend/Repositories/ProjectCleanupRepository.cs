using System.Security.Cryptography;
using backend.Models;
using backend.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class ProjectCleanupRepository(MongoDbContext db)
{
    public static IReadOnlyList<string> CascadeCollections { get; } =
        Array.AsReadOnly(
            new[]
            {
                "relayAllowancePeriods",
                "relayEntitlements",
                "relayBillingLedgers",
                "relayProjectAdmissions",
                "contracts",
            }
        );

    private IMongoDatabase Database => db.Projects.Database;
    private IMongoCollection<ProjectDocument> Projects =>
        db.Projects.WithWriteConcern(WriteConcern.WMajority.With(journal: true));
    private IMongoCollection<ProjectCleanupDocument> Operations =>
        Database
            .GetCollection<ProjectCleanupDocument>("projectCleanupOperations")
            .WithWriteConcern(WriteConcern.WMajority.With(journal: true));

    public Task<ProjectCleanupDocument?> GetAsync(string id, CancellationToken ct) =>
        Operations.Find(item => item.Id == id).FirstOrDefaultAsync(ct)!;

    public async Task<IReadOnlyList<ProjectCleanupCandidate>> CandidatesAsync(
        DateTime now,
        int take,
        DateTime? afterCreatedAt,
        string? afterId,
        CancellationToken ct
    )
    {
        if (take is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(take));
        if ((afterCreatedAt is null) != (afterId is null))
            throw new ArgumentException("Both cursor fields are required.");
        var filter = Builders<ProjectDocument>.Filter.Where(project =>
            !project.IsDeleting
            && project.ContractSchemaVersion == 1
            && project.CreatedAt > DateTime.MinValue
            && project.CreatedAt
                <= now.AddDays(-Pusharoo.Retention.UnusedProjectRetentionPolicy.RetentionDays)
        );
        if (afterCreatedAt is { } date)
            filter &=
                Builders<ProjectDocument>.Filter.Gt(project => project.CreatedAt, date)
                | Builders<ProjectDocument>.Filter.Eq(project => project.CreatedAt, date)
                    & Builders<ProjectDocument>.Filter.Gt(project => project.Id, afterId);
        return await db
            .Projects.Find(filter)
            .SortBy(project => project.CreatedAt)
            .ThenBy(project => project.Id)
            .Limit(take)
            .Project(project => new ProjectCleanupCandidate(project.Id, project.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectCleanupOperationSummary>> PendingAsync(
        int take,
        CancellationToken ct
    )
    {
        if (take is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(take));
        return await Operations
            .Find(operation =>
                operation.RunnerToken == null
                && (operation.Status == "prepared" || operation.Status == "admitted")
            )
            .SortBy(operation => operation.CheckedAtUtc)
            .ThenBy(operation => operation.Id)
            .Limit(take)
            .Project(operation => new ProjectCleanupOperationSummary(
                operation.Id,
                operation.OperationId,
                operation.CheckedAtUtc
            ))
            .ToListAsync(ct);
    }

    public async Task<ProjectCleanupRecord[]> FreezeRecordsAsync(
        string projectId,
        CancellationToken ct
    )
    {
        var result = new List<ProjectCleanupRecord>();
        foreach (var name in CascadeCollections)
        {
            var documents = await Database
                .GetCollection<BsonDocument>(name)
                .Find(Owned(projectId))
                .Limit(1001)
                .ToListAsync(ct);
            if (documents.Count > 1000)
                throw new IOException("Project cleanup metadata exceeds the bounded manifest.");
            foreach (var document in documents)
            {
                if (!KnownFields(name, document))
                    throw new IOException(
                        "Unknown project cleanup schema requires reconciliation."
                    );
                result.Add(
                    new()
                    {
                        Collection = name,
                        Id = document["_id"],
                        Sha256 = Hash(document),
                    }
                );
            }
        }
        return result.ToArray();
    }

    public async Task<bool> PrepareAsync(ProjectCleanupDocument operation, CancellationToken ct)
    {
        var current = await GetAsync(operation.Id, ct);
        if (current is null)
        {
            try
            {
                await Operations.InsertOneAsync(operation, cancellationToken: ct);
                return true;
            }
            catch (MongoWriteException error)
                when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return false;
            }
        }
        return (
                await Operations.ReplaceOneAsync(
                    item =>
                        item.Id == operation.Id
                        && item.Status == "deferred"
                        && item.RunnerToken == null,
                    operation,
                    cancellationToken: ct
                )
            ).MatchedCount == 1;
    }

    public async Task<bool> AdmitAsync(
        ProjectCleanupDocument operation,
        DateTime now,
        bool useDatabaseTime,
        CancellationToken ct
    )
    {
        var filter =
            Builders<ProjectDocument>.Filter.Where(project =>
                project.Id == operation.Id
                && !project.IsDeleting
                && project.ContractSchemaVersion == 1
                && project.CleanupOperationId == null
                && project.ContractWriteToken == operation.ParentWriteToken
                && project.CreatedAt == operation.CreatedAtUtc
            )
            & new BsonDocument(
                "$expr",
                new BsonDocument(
                    "$gte",
                    new BsonArray
                    {
                        useDatabaseTime ? (BsonValue)"$$NOW" : now,
                        operation.DeadlineUtc,
                    }
                )
            );
        return (
                await Projects.UpdateOneAsync(
                    filter,
                    Builders<ProjectDocument>
                        .Update.Set(project => project.IsDeleting, true)
                        .Set(project => project.CleanupOperationId, operation.OperationId)
                        .Unset(project => project.ContractWriteToken),
                    cancellationToken: ct
                )
            ).MatchedCount == 1;
    }

    public async Task<bool> AbortAdmissionAsync(string id, string parentToken)
    {
        return (
                await Projects.UpdateOneAsync(
                    project =>
                        project.Id == id
                        && !project.IsDeleting
                        && project.ContractWriteToken == parentToken,
                    Builders<ProjectDocument>.Update.Unset(project => project.ContractWriteToken),
                    cancellationToken: CancellationToken.None
                )
            ).MatchedCount == 1;
    }

    public async Task<ProjectCleanupDocument?> ClaimAsync(
        string id,
        string runner,
        CancellationToken ct
    ) =>
        await Operations.FindOneAndUpdateAsync(
            item =>
                item.Id == id
                && item.RunnerToken == null
                && (item.Status == "prepared" || item.Status == "admitted"),
            Builders<ProjectCleanupDocument>.Update.Set(item => item.RunnerToken, runner),
            new() { ReturnDocument = ReturnDocument.After },
            ct
        );

    public async Task RecordAdmissionAsync(
        ProjectCleanupDocument operation,
        DateTime now,
        CancellationToken ct
    )
    {
        if (operation.AdmittedAtUtc is not null)
            return;
        var result = await Operations.UpdateOneAsync(
            item =>
                item.Id == operation.Id
                && item.OperationId == operation.OperationId
                && item.RunnerToken == operation.RunnerToken
                && item.AdmittedAtUtc == null,
            Builders<ProjectCleanupDocument>.Update.Set(item => item.AdmittedAtUtc, now),
            cancellationToken: ct
        );
        if (result.MatchedCount != 1)
            throw new IOException("Cleanup admission timestamp requires reconciliation.");
    }

    public async Task ProgressAsync(
        ProjectCleanupDocument operation,
        string status,
        int step,
        DateTime now,
        CancellationToken ct
    )
    {
        var update = Builders<ProjectCleanupDocument>
            .Update.Set(item => item.Status, status)
            .Set(item => item.Step, step);
        if (status == "completed")
            update = update.Set(item => item.CompletedAtUtc, now);
        var changed = await Operations.UpdateOneAsync(
            item =>
                item.Id == operation.Id
                && item.OperationId == operation.OperationId
                && item.RunnerToken == operation.RunnerToken,
            update,
            cancellationToken: ct
        );
        if (changed.MatchedCount != 1)
            throw new IOException("Cleanup runner ownership changed.");
    }

    public async Task ReleaseRunnerAsync(ProjectCleanupDocument operation)
    {
        var result = await Operations.UpdateOneAsync(
            item => item.Id == operation.Id && item.RunnerToken == operation.RunnerToken,
            Builders<ProjectCleanupDocument>.Update.Unset(item => item.RunnerToken),
            cancellationToken: CancellationToken.None
        );
        if (result.MatchedCount != 1)
            throw new IOException("Cleanup runner release requires reconciliation.");
    }

    public async Task DeleteManifestAsync(
        ProjectCleanupDocument operation,
        string name,
        CancellationToken ct
    )
    {
        var collection = Database
            .GetCollection<BsonDocument>(name)
            .WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        foreach (var record in operation.Records.Where(item => item.Collection == name))
        {
            var current = await collection
                .Find(new BsonDocument("_id", record.Id))
                .FirstOrDefaultAsync(ct);
            if (current is null)
                continue;
            if (Hash(current) != record.Sha256)
                throw new IOException("A cleanup record changed after admission.");
            if ((await collection.DeleteOneAsync(current, ct)).DeletedCount != 1)
                throw new IOException("A cleanup record changed during deletion.");
        }
    }

    public async Task VerifyEmptyAsync(ProjectCleanupDocument operation, CancellationToken ct)
    {
        foreach (
            var name in CascadeCollections.Concat(
                new[]
                {
                    "contractArtifacts",
                    "deployments",
                    "eventSubscriptions",
                    "eventReplayJobs",
                    "relayPaymentIntents",
                    "relayPayments",
                    "relayPurchasedPeriods",
                    "relayPeriodUpgrades",
                    "relayBillingDecisions",
                    "relayEntitlementHistory",
                    "relayPaymentResolutions",
                    "relayLegacyBillingSnapshots",
                }
            )
        )
            if (
                await Database
                    .GetCollection<BsonDocument>(name)
                    .Find(Owned(operation.Id))
                    .Limit(1)
                    .AnyAsync(ct)
            )
                throw new IOException("New project-owned state appeared after cleanup admission.");
    }

    public Task<DeleteResult> DeleteParentAsync(
        string id,
        string? operationId,
        CancellationToken ct
    )
    {
        var filter = Builders<ProjectDocument>.Filter.Eq(project => project.Id, id);
        if (operationId is not null)
            filter &= Builders<ProjectDocument>.Filter.Where(project =>
                project.IsDeleting && project.CleanupOperationId == operationId
            );
        return Projects.DeleteOneAsync(filter, ct);
    }

    private static BsonDocument Owned(string id) =>
        new(
            "$or",
            new BsonArray
            {
                new BsonDocument("projectId", id),
                new BsonDocument("_id", id),
                new BsonDocument(
                    "_id",
                    new BsonRegularExpression(
                        "^" + System.Text.RegularExpressions.Regex.Escape(id) + ":"
                    )
                ),
            }
        );

    private static string Hash(BsonDocument doc) =>
        Convert.ToHexStringLower(SHA256.HashData(doc.ToBson()));

    private static bool KnownFields(string name, BsonDocument doc)
    {
        var fields = name switch
        {
            "contracts" =>
                "_id projectId name normalizedName description createdAt updatedAt idempotencyKey idempotencyPayloadHash testnetDeploymentWriteToken mainnetDeploymentWriteToken",
            "relayAllowancePeriods" => "_id closed periodEndsAt eventsUsed chargedDeliveries",
            "relayBillingLedgers" => "_id version openIntent",
            "relayEntitlements" =>
                "_id projectId network plan packageId packageTerms purchasedPeriodId appliedPaymentAllocations status periodStart periodEndsAt maxActiveSubscriptions maxEvents eventsUsed paidUntil graceEndsAt lastPaymentId updatedAt version lifecycleRevision paymentGrants",
            "relayProjectAdmissions" =>
                "_id version mainnet testnet slots transition mutation publication selections lastTransition nextCheckAt attemptAdmissions validUntil",
            _ => "",
        };
        if (!doc.Names.All(fields.Split(' ').Contains))
            return false;
        if (name == "relayProjectAdmissions")
        {
            var transition = doc.GetValue("lastTransition", BsonNull.Value);
            if (
                !transition.IsBsonNull
                && (
                    !transition.IsBsonDocument
                    || !transition.AsBsonDocument.Names.All(
                        new[] { "_id", "Id", "AppliedAt", "PausedSubscriptionIds" }.Contains
                    )
                    || transition.AsBsonDocument.GetValue("PausedSubscriptionIds", BsonNull.Value)
                        is not BsonArray { Count: 0 }
                )
            )
                return false;
            foreach (var field in new[] { "mainnet", "testnet" })
            {
                var budget = doc.GetValue(field, BsonNull.Value);
                if (budget.IsBsonNull)
                    continue;
                if (
                    !budget.IsBsonDocument
                    || !budget.AsBsonDocument.Names.All(
                        new[] { "entitlement", "periodId", "eventsUsed", "receipts" }.Contains
                    )
                )
                    return false;
                var entitlement = budget.AsBsonDocument.GetValue("entitlement", BsonNull.Value);
                if (
                    !entitlement.IsBsonDocument
                    || !KnownFields("relayEntitlements", entitlement.AsBsonDocument)
                )
                    return false;
            }
        }
        return true;
    }
}
