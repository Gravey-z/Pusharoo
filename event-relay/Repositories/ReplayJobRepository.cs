using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public sealed class ReplayJobRepository(MongoDbContext db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private IMongoCollection<ReplayJobDocument> Jobs =>
        db.ReplayJobs.WithWriteConcern(WriteConcern.WMajority.With(journal: true));

    public Task<List<ReplayJobDocument>> ListAsync(
        string project,
        string subscription,
        string network,
        CancellationToken ct
    ) =>
        Jobs.Find(x =>
                x.ProjectId == project && x.SubscriptionId == subscription && x.Network == network
            )
            .SortByDescending(x => x.CreatedAt)
            .Limit(50)
            .ToListAsync(ct);

    public Task<List<ReplayJobDocument>> ListProjectAsync(
        string project,
        string network,
        CancellationToken ct
    ) =>
        Jobs.Find(x => x.ProjectId == project && x.Network == network)
            .SortByDescending(x => x.CreatedAt)
            .Limit(50)
            .ToListAsync(ct);

    public Task CreateAsync(ReplayJobDocument job, CancellationToken ct) =>
        Jobs.InsertOneAsync(job, cancellationToken: ct);

    public async Task<ReplayJobDocument?> GetAsync(string id, CancellationToken ct) =>
        await Jobs.Find(x => x.Id == id).FirstOrDefaultAsync(ct);

    private static FilterDefinition<ReplayJobDocument> Owned(ReplayJobDocument job) =>
        Builders<ReplayJobDocument>.Filter.Eq(x => x.Id, job.Id)
        & Builders<ReplayJobDocument>.Filter.Eq(x => x.Owner, job.Owner)
        & Builders<ReplayJobDocument>.Filter.Eq(x => x.Status, "running")
        & new BsonDocument(
            "$expr",
            new BsonDocument("$gt", new BsonArray { "$leaseUntil", "$$NOW" })
        );

    public async Task<ReplayJobDocument?> ClaimAsync(string network, CancellationToken ct)
    {
        var filter = new BsonDocument
        {
            { "network", network },
            { "status", "running" },
            {
                "$expr",
                new BsonDocument(
                    "$and",
                    new BsonArray
                    {
                        new BsonDocument(
                            "$lte",
                            new BsonArray
                            {
                                new BsonDocument(
                                    "$ifNull",
                                    new BsonArray
                                    {
                                        "$leaseUntil",
                                        new BsonDateTime(DateTime.UnixEpoch),
                                    }
                                ),
                                "$$NOW",
                            }
                        ),
                        new BsonDocument("$lte", new BsonArray { "$nextAttemptAt", "$$NOW" }),
                    }
                )
            },
        };

        PipelineDefinition<ReplayJobDocument, ReplayJobDocument> pipeline = new[]
        {
            new BsonDocument(
                "$set",
                new BsonDocument
                {
                    { "owner", Guid.NewGuid().ToString("N") },
                    { "leaseUntil", new BsonDocument("$add", new BsonArray { "$$NOW", 60000 }) },
                }
            ),
        };

        return await Jobs.FindOneAndUpdateAsync(
            filter,
            Builders<ReplayJobDocument>.Update.Pipeline(pipeline),
            new FindOneAndUpdateOptions<ReplayJobDocument>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<ReplayJobDocument>.Sort.Ascending(x => x.NextAttemptAt),
            },
            ct
        );
    }

    public async Task<bool> RenewAsync(ReplayJobDocument job, CancellationToken ct)
    {
        PipelineDefinition<ReplayJobDocument, ReplayJobDocument> pipeline = new[]
        {
            new BsonDocument(
                "$set",
                new BsonDocument(
                    "leaseUntil",
                    new BsonDocument("$add", new BsonArray { "$$NOW", 60000 })
                )
            ),
        };

        return (
                await Jobs.UpdateOneAsync(
                    Owned(job),
                    Builders<ReplayJobDocument>.Update.Pipeline(pipeline),
                    cancellationToken: ct
                )
            ).MatchedCount == 1;
    }

    public async Task<bool> AdvanceAsync(ReplayJobDocument job, uint height, CancellationToken ct)
    {
        var finished = height == job.ToBlock;

        return (
                await Jobs.UpdateOneAsync(
                    Owned(job) & Builders<ReplayJobDocument>.Filter.Eq(x => x.NextBlock, height),
                    Builders<ReplayJobDocument>
                        .Update.Set(x => x.NextBlock, checked(height + 1))
                        .Set(x => x.UpdatedAt, clock.GetUtcNow().UtcDateTime)
                        .Set(x => x.Status, finished ? "completed" : "running")
                        .Set(x => x.Active, !finished)
                        .Set(x => x.Error, null),
                    cancellationToken: ct
                )
            ).MatchedCount == 1;
    }

    public async Task FailAsync(
        ReplayJobDocument job,
        ScannerBlockingError error,
        bool blocked,
        CancellationToken ct
    ) =>
        await Jobs.UpdateOneAsync(
            Owned(job),
            Builders<ReplayJobDocument>
                .Update.Set(x => x.Error, error)
                .Set(x => x.Status, blocked ? "blocked" : "running")
                .Set(x => x.NextAttemptAt, clock.GetUtcNow().UtcDateTime.AddSeconds(15))
                .Set(x => x.UpdatedAt, clock.GetUtcNow().UtcDateTime)
                .Unset(x => x.Owner)
                .Unset(x => x.LeaseUntil),
            cancellationToken: ct
        );

    public async Task ReleaseAsync(ReplayJobDocument job, CancellationToken ct) =>
        await Jobs.UpdateOneAsync(
            Owned(job),
            Builders<ReplayJobDocument>
                .Update.Unset(x => x.Owner)
                .Unset(x => x.LeaseUntil)
                .Set(x => x.NextAttemptAt, clock.GetUtcNow().UtcDateTime.AddSeconds(2)),
            cancellationToken: ct
        );

    public async Task<bool> ActionAsync(string id, string action, CancellationToken ct)
    {
        var allowed = action switch
        {
            "pause" => new[] { "running" },
            "resume" => new[] { "paused", "blocked" },
            "cancel" => new[] { "running", "paused", "blocked" },
            _ => Array.Empty<string>(),
        };

        var status =
            action == "pause" ? "paused"
            : action == "resume" ? "running"
            : "cancelled";

        return (
                await Jobs.UpdateOneAsync(
                    x => x.Id == id && allowed.Contains(x.Status),
                    Builders<ReplayJobDocument>
                        .Update.Set(x => x.Status, status)
                        .Set(x => x.Active, action != "cancel")
                        .Set(x => x.Error, null)
                        .Set(x => x.UpdatedAt, clock.GetUtcNow().UtcDateTime)
                        .Set(x => x.NextAttemptAt, clock.GetUtcNow().UtcDateTime)
                        .Unset(x => x.Owner)
                        .Unset(x => x.LeaseUntil),
                    cancellationToken: ct
                )
            ).MatchedCount == 1;
    }
}
