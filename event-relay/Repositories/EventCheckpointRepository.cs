using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public sealed class EventCheckpointRepository(MongoDbContext db) : IEventCheckpointRepository
{
    private IMongoCollection<EventCheckpointDocument> Collection =>
        db.Checkpoints.WithWriteConcern(WriteConcern.WMajority.With(journal: true));

    public Task<EventCheckpointDocument?> GetAsync(
        string id,
        CancellationToken cancellationToken
    ) => Collection.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<EventCheckpointDocument?> TryAcquireAsync(
        string id,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken
    )
    {
        // Insert first, then acquire with a conditional update; never upsert over another owner's lease.
        try
        {
            await Collection.InsertOneAsync(
                new EventCheckpointDocument { Id = id },
                cancellationToken: cancellationToken
            );
        }
        catch (MongoWriteException ex)
            when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

        var available = new BsonDocument(
            "$expr",
            new BsonDocument(
                "$lte",
                new BsonArray
                {
                    new BsonDocument(
                        "$ifNull",
                        new BsonArray { "$scannerLeaseUntil", new BsonDateTime(DateTime.UnixEpoch) }
                    ),
                    "$$NOW"
                }
            )
        );
        var filter = Builders<EventCheckpointDocument>.Filter.Eq(x => x.Id, id) & available;

        return await Collection.FindOneAndUpdateAsync(
            filter,
            LeaseUpdate(owner, duration),
            new FindOneAndUpdateOptions<EventCheckpointDocument>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken
        );
    }

    private static UpdateDefinition<EventCheckpointDocument> LeaseUpdate(
        string owner,
        TimeSpan duration
    ) =>
        new PipelineUpdateDefinition<EventCheckpointDocument>(
            new[]
            {
                new BsonDocument(
                    "$set",
                    new BsonDocument
                    {
                        { "scannerOwner", owner },
                        {
                            "scannerLeaseUntil",
                            new BsonDocument(
                                "$add",
                                new BsonArray { "$$NOW", (long)duration.TotalMilliseconds }
                            )
                        }
                    }
                )
            }
        );

    private static FilterDefinition<EventCheckpointDocument> Owned(string id, string owner) =>
        Builders<EventCheckpointDocument>.Filter.Eq(x => x.Id, id)
        & Builders<EventCheckpointDocument>.Filter.Eq(x => x.ScannerOwner, owner)
        & new BsonDocument(
            "$expr",
            new BsonDocument("$gt", new BsonArray { "$scannerLeaseUntil", "$$NOW" })
        );

    public async Task<bool> RenewAsync(
        string id,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken
    ) =>
        (
            await Collection.UpdateOneAsync(
                Owned(id, owner),
                LeaseUpdate(owner, duration),
                cancellationToken: cancellationToken
            )
        ).MatchedCount == 1;

    public async Task<bool> AdvanceAsync(
        string id,
        string owner,
        uint? expectedNextBlock,
        uint nextBlock,
        CancellationToken cancellationToken
    )
    {
        if (expectedNextBlock is not null && nextBlock != checked(expectedNextBlock.Value + 1))
            throw new InvalidOperationException(
                "Checkpoint advancement must complete exactly one block."
            );

        var filter =
            Owned(id, owner)
            & Builders<EventCheckpointDocument>.Filter.Eq(x => x.NextBlock, expectedNextBlock)
            & Builders<EventCheckpointDocument>.Filter.Eq(x => x.BlockingError, null);
        var update = Builders<EventCheckpointDocument>
            .Update.Set(x => x.NextBlock, nextBlock)
            .CurrentDate(x => x.UpdatedAt);

        if (expectedNextBlock is not null)
            update = update.CurrentDate(x => x.LastSuccessfulScanAt);

        return (
                await Collection.UpdateOneAsync(
                    filter,
                    update,
                    cancellationToken: cancellationToken
                )
            ).MatchedCount == 1;
    }

    public async Task<bool> BlockAsync(
        string id,
        string owner,
        ScannerBlockingError error,
        CancellationToken cancellationToken
    ) =>
        (
            await Collection.UpdateOneAsync(
                Owned(id, owner),
                Builders<EventCheckpointDocument>.Update.Set(x => x.BlockingError, error),
                cancellationToken: cancellationToken
            )
        ).MatchedCount == 1;

    public async Task<bool> RecordTipAsync(
        string id,
        string owner,
        uint tip,
        uint confirmed,
        CancellationToken ct
    ) =>
        (
            await Collection.UpdateOneAsync(
                Owned(id, owner),
                Builders<EventCheckpointDocument>
                    .Update.Set(x => x.NetworkTip, tip)
                    .Set(x => x.ConfirmedTip, confirmed)
                    .CurrentDate(x => x.LastPollAt),
                cancellationToken: ct
            )
        ).MatchedCount == 1;

    public async Task ReleaseAsync(string id, string owner, CancellationToken cancellationToken) =>
        await Collection.UpdateOneAsync(
            x => x.Id == id && x.ScannerOwner == owner,
            Builders<EventCheckpointDocument>
                .Update.Unset(x => x.ScannerOwner)
                .Unset(x => x.ScannerLeaseUntil),
            cancellationToken: cancellationToken
        );
}
