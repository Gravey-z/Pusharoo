using System.Text.Json;
using backend.Services;
using MongoDB.Bson;
using MongoDB.Driver;
using static Pusharoo.Migration.MigrationIdentity;

namespace Pusharoo.Migration;

public sealed class MigrationRepository(IMongoDatabase database)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new BsonValueJsonConverter(), new BsonDocumentJsonConverter() },
    };

    private IMongoCollection<BsonDocument> Collection(string name) =>
        database.GetCollection<BsonDocument>(name);

    public async Task<MigrationSnapshot> ReadAsync(CancellationToken ct)
    {
        var projects = ReadAllAsync("projects", ct);
        var contracts = ReadAllAsync("contracts", ct);
        var artifacts = ReadAllAsync("contractArtifacts", ct);
        var deployments = ReadAllAsync("deployments", ct);
        var journals = ReadAllAsync("contractMigrationProjects", ct);
        var indexes = Task.WhenAll(
            new[] { "projects", "contracts", "contractArtifacts", "deployments" }.Select(
                async name => new IndexInventory(name, await ReadIndexesAsync(name, ct))
            )
        );
        await Task.WhenAll(projects, contracts, artifacts, deployments, journals, indexes);
        return new()
        {
            Projects = await projects,
            Contracts = await contracts,
            Artifacts = await artifacts,
            Deployments = await deployments,
            Journals = await journals,
            Indexes = await indexes,
        };
    }

    private async Task<IReadOnlyList<BsonDocument>> ReadAllAsync(
        string name,
        CancellationToken ct
    ) =>
        await Collection(name)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BsonDocument>> ReadIndexesAsync(
        string name,
        CancellationToken ct
    )
    {
        try
        {
            using var cursor = await Collection(name).Indexes.ListAsync(ct);
            return await cursor.ToListAsync(ct);
        }
        catch (MongoCommandException error) when (error.Code == 26)
        {
            return [];
        }
    }

    public async Task<string> AcquireAsync(CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        try
        {
            await Collection("contractMigrationState")
                .UpdateOneAsync(
                    new BsonDocument
                    {
                        { "_id", "multi-contract-v1" },
                        { "leaseToken", BsonNull.Value },
                    },
                    new BsonDocument(
                        "$set",
                        new BsonDocument
                        {
                            { "leaseToken", token },
                            { "status", "running" },
                            { "version", 1 },
                        }
                    ),
                    new UpdateOptions { IsUpsert = true },
                    ct
                );
            return token;
        }
        catch (MongoWriteException error)
            when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new MigrationBlockedException(
                "Another migration owns the offline lock. Inspect it before explicitly releasing a stale lock."
            );
        }
    }

    public async Task AssertOwnerAsync(string token, CancellationToken ct)
    {
        if (
            !await Collection("contractMigrationState")
                .Find(
                    new BsonDocument
                    {
                        { "_id", "multi-contract-v1" },
                        { "leaseToken", token },
                        { "status", "running" },
                        { "version", 1 },
                    }
                )
                .AnyAsync(ct)
        )
            throw new MigrationBlockedException(
                "Migration lock ownership changed. This process must stop before another run can continue."
            );
    }

    public Task ReleaseAsync(string token, string status) =>
        Collection("contractMigrationState")
            .UpdateOneAsync(
                new BsonDocument { { "_id", "multi-contract-v1" }, { "leaseToken", token } },
                new BsonDocument
                {
                    { "$set", new BsonDocument("status", status) },
                    { "$unset", new BsonDocument("leaseToken", "") },
                }
            );

    public async Task ReleaseStaleLockAsync(string expectedToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedToken))
            throw new MigrationBlockedException("An exact expected lock token is required.");
        var result = await Collection("contractMigrationState")
            .UpdateOneAsync(
                new BsonDocument
                {
                    { "_id", "multi-contract-v1" },
                    { "leaseToken", expectedToken },
                },
                new BsonDocument
                {
                    { "$set", new BsonDocument("status", "blocked") },
                    { "$unset", new BsonDocument("leaseToken", "") },
                },
                cancellationToken: ct
            );
        if (result.MatchedCount != 1)
            throw new MigrationBlockedException(
                "The lock changed or the expected token does not match."
            );
    }

    public Task<BsonDocument?> GetStateAsync(CancellationToken ct) =>
        Collection("contractMigrationState")
            .Find(new BsonDocument("_id", "multi-contract-v1"))
            .FirstOrDefaultAsync(ct)!;

    public async Task SavePlanAsync(ProjectPlan plan, CancellationToken ct)
    {
        var journal = new BsonDocument
        {
            { "_id", plan.ProjectId },
            { "version", 1 },
            { "status", "planned" },
            { "plan", BsonDocument.Parse(JsonSerializer.Serialize(plan, JsonOptions)) },
        };
        var existing = await Collection("contractMigrationProjects")
            .Find(new BsonDocument("_id", plan.ProjectId))
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (!existing["plan"].Equals(journal["plan"]))
                throw new MigrationBlockedException(
                    "An established project mapping cannot be replaced."
                );
            return;
        }
        await Collection("contractMigrationProjects")
            .InsertOneAsync(journal, cancellationToken: ct);
    }

    public Task SetProgressAsync(string projectId, string status, CancellationToken ct) =>
        Collection("contractMigrationProjects")
            .UpdateOneAsync(
                new BsonDocument("_id", projectId),
                new BsonDocument("$set", new BsonDocument("status", status)),
                cancellationToken: ct
            );

    public Task BlockProjectAsync(string projectId, CancellationToken ct) =>
        Collection("projects")
            .UpdateOneAsync(
                new BsonDocument("_id", projectId),
                new BsonDocument("$set", new BsonDocument("contractMigrationState", "blocked")),
                cancellationToken: ct
            );

    public async Task EnsureContractAsync(ContractPlan plan, CancellationToken ct)
    {
        var existing = await Collection("contracts")
            .Find(new BsonDocument("_id", plan.Id))
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (Parent(existing) != plan.ProjectId || Text(existing, "name") != plan.Name)
                throw new MigrationBlockedException("The planned contract identity/name changed.");
            return;
        }
        if (plan.Existing)
            throw new MigrationBlockedException(
                "An explicitly selected contract no longer exists."
            );
        await Collection("contracts")
            .InsertOneAsync(
                new BsonDocument
                {
                    { "_id", plan.Id },
                    { "projectId", plan.ProjectId },
                    { "name", plan.Name },
                    { "normalizedName", plan.Name.ToUpperInvariant() },
                    { "createdAt", plan.CreatedAt },
                    { "updatedAt", plan.CreatedAt },
                },
                cancellationToken: ct
            );
    }

    public async Task BackfillAsync(RecordPlan plan, CancellationToken ct)
    {
        var collection = Collection(plan.Kind == "artifact" ? "contractArtifacts" : "deployments");
        var current = await collection
            .Find(new BsonDocument("_id", plan.Id))
            .FirstOrDefaultAsync(ct);
        if (
            current is null
            || Parent(current) != plan.ProjectId
            || Hash(current) != plan.SourceHash
            || (
                Text(current, "contractId") != plan.OriginalContractId
                && Text(current, "contractId") != plan.ContractId
            )
        )
            throw new MigrationBlockedException(
                "A source record changed after its mapping was frozen."
            );
        var values = new BsonDocument("contractId", plan.ContractId);
        if (plan.AuditLegacy)
            values["legacyContractMappingVerified"] = true;
        await ReplaceFieldsAsync(collection, current, values, ct);
    }

    public async Task CompleteProjectAsync(ProjectPlan plan, CancellationToken ct)
    {
        var project = await Collection("projects")
            .Find(new BsonDocument("_id", plan.ProjectId))
            .FirstOrDefaultAsync(ct);
        if (project is null || Hash(project) != plan.SourceHash)
            throw new MigrationBlockedException("The project changed after planning.");
        await ReplaceFieldsAsync(
            Collection("projects"),
            project,
            new BsonDocument
            {
                { "contractSchemaVersion", 1 },
                { "contractMigrationState", "ready" },
            },
            ct
        );
        await SetProgressAsync(plan.ProjectId, "ready", ct);
    }

    private static async Task ReplaceFieldsAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument current,
        BsonDocument values,
        CancellationToken ct
    )
    {
        var filter = new BsonDocument
        {
            { "_id", current["_id"] },
            {
                "$expr",
                new BsonDocument(
                    "$eq",
                    new BsonArray
                    {
                        new BsonDocument(
                            "$cmp",
                            new BsonArray { "$$ROOT", new BsonDocument("$literal", current) }
                        ),
                        0,
                    }
                )
            },
        };
        var result = await collection.UpdateOneAsync(
            filter,
            new BsonDocument("$set", values),
            cancellationToken: ct
        );
        if (result.MatchedCount != 1)
            throw new MigrationBlockedException(
                "A concurrent writer changed a migration source record."
            );
    }

    public async Task CreateIndexesAsync(CancellationToken ct)
    {
        await Collection("contracts")
            .Indexes.CreateManyAsync(
                new[]
                {
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument { { "projectId", 1 }, { "normalizedName", 1 } },
                        new CreateIndexOptions { Name = "contract_project_name", Unique = true }
                    ),
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument("idempotencyKey", 1),
                        new CreateIndexOptions
                        {
                            Name = "contract_creation_key",
                            Unique = true,
                            Sparse = true,
                        }
                    ),
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument
                        {
                            { "projectId", 1 },
                            { "createdAt", 1 },
                            { "_id", 1 },
                        },
                        new CreateIndexOptions { Name = "contract_project_list" }
                    ),
                },
                ct
            );
        await Collection("contractArtifacts")
            .Indexes.CreateManyAsync(
                new[]
                {
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument
                        {
                            { "projectId", 1 },
                            { "contractId", 1 },
                            { "version", 1 },
                        },
                        new CreateIndexOptions
                        {
                            Name = "artifact_project_contract_version",
                            Unique = true,
                        }
                    ),
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument
                        {
                            { "projectId", 1 },
                            { "contractId", 1 },
                            { "createdAt", -1 },
                            { "_id", -1 },
                        },
                        new CreateIndexOptions { Name = "artifact_contract_history" }
                    ),
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument
                        {
                            { "projectId", 1 },
                            { "createdAt", -1 },
                            { "_id", -1 },
                        },
                        new CreateIndexOptions { Name = "artifact_project_history" }
                    ),
                },
                ct
            );
        await Collection("deployments")
            .Indexes.CreateManyAsync(
                new[]
                {
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument("transactionId", 1),
                        new CreateIndexOptions { Unique = true, Sparse = true }
                    ),
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument("activeAttemptKey", 1),
                        new CreateIndexOptions { Unique = true, Sparse = true }
                    ),
                    new CreateIndexModel<BsonDocument>(
                        new BsonDocument
                        {
                            { "projectId", 1 },
                            { "contractId", 1 },
                            { "network", 1 },
                            { "createdAt", -1 },
                            { "_id", -1 },
                        },
                        new CreateIndexOptions { Name = "deployment_contract_history" }
                    ),
                },
                ct
            );
    }

    public async Task DropLegacyArtifactIndexesAsync(CancellationToken ct)
    {
        var indexes = await ReadIndexesAsync("contractArtifacts", ct);
        foreach (
            var index in indexes.Where(item =>
                item.GetValue("unique", false).ToBoolean()
                && item["key"].AsBsonDocument.Names.ToHashSet().SetEquals(["projectId", "version"])
            )
        )
            await Collection("contractArtifacts").Indexes.DropOneAsync(index["name"].AsString, ct);
    }
}
