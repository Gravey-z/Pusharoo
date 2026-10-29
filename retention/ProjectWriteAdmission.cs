using MongoDB.Bson;
using MongoDB.Driver;

namespace Pusharoo.Retention;

public sealed class ProjectWriteAdmission(IMongoDatabase database)
{
    public const string CollectionName = "projectWriteAdmissions";

    private IMongoCollection<BsonDocument> Collection =>
        database
            .GetCollection<BsonDocument>(CollectionName)
            .WithWriteConcern(WriteConcern.WMajority.With(journal: true));

    public async Task<IAsyncDisposable> EnterAsync(string projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("A project identity is required.", nameof(projectId));
        await EnsureAsync(projectId, ct);
        var token = Guid.NewGuid().ToString("N");
        var filter = new BsonDocument
        {
            { "_id", projectId },
            { "cleanupToken", BsonNull.Value },
            { "retired", new BsonDocument("$ne", true) },
        };
        var result = await Collection.UpdateOneAsync(
            filter,
            new BsonDocument("$addToSet", new BsonDocument("writers", token)),
            cancellationToken: ct
        );
        if (result.MatchedCount == 0)
            throw new ProjectWriteBlockedException();
        return new WriterLease(Collection, projectId, token);
    }

    public async Task<bool> TryExcludeWritersAsync(
        string projectId,
        string token,
        CancellationToken ct
    )
    {
        await EnsureAsync(projectId, ct);
        var filter = new BsonDocument
        {
            { "_id", projectId },
            { "cleanupToken", BsonNull.Value },
            { "writers.0", new BsonDocument("$exists", false) },
        };
        return (
                await Collection.UpdateOneAsync(
                    filter,
                    new BsonDocument("$set", new BsonDocument("cleanupToken", token)),
                    cancellationToken: ct
                )
            ).MatchedCount == 1;
    }

    public async Task RetireAsync(string projectId, string token, CancellationToken ct)
    {
        var result = await Collection.UpdateOneAsync(
            new BsonDocument
            {
                { "_id", projectId },
                { "cleanupToken", token },
                { "writers.0", new BsonDocument("$exists", false) },
            },
            new BsonDocument("$set", new BsonDocument("retired", true)),
            cancellationToken: ct
        );
        if (result.MatchedCount != 1)
            throw new IOException(
                "Project deletion admission ownership changed; reconcile the stored token."
            );
    }

    public async Task ReleaseExclusionAsync(string projectId, string token)
    {
        var result = await Collection.UpdateOneAsync(
            new BsonDocument { { "_id", projectId }, { "cleanupToken", token } },
            new BsonDocument("$unset", new BsonDocument("cleanupToken", "")),
            cancellationToken: CancellationToken.None
        );
        if (result.MatchedCount != 1)
            throw new IOException(
                "Project deletion admission could not be released with its original token."
            );
    }

    public Task<bool> IsRetiredAsync(string projectId, CancellationToken ct) =>
        Collection
            .Find(new BsonDocument { { "_id", projectId }, { "retired", true } })
            .AnyAsync(ct);

    private async Task EnsureAsync(string projectId, CancellationToken ct)
    {
        try
        {
            await Collection.UpdateOneAsync(
                new BsonDocument("_id", projectId),
                new BsonDocument("$setOnInsert", new BsonDocument("writers", new BsonArray())),
                new UpdateOptions { IsUpsert = true },
                ct
            );
        }
        catch (MongoWriteException error)
            when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    private sealed class WriterLease(
        IMongoCollection<BsonDocument> collection,
        string projectId,
        string token
    ) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            var result = await collection.UpdateOneAsync(
                new BsonDocument { { "_id", projectId }, { "writers", token } },
                new BsonDocument("$pull", new BsonDocument("writers", token)),
                cancellationToken: CancellationToken.None
            );
            if (result.MatchedCount != 1)
                throw new IOException(
                    "Project writer admission could not be released with its original token."
                );
        }
    }
}

public sealed class ProjectWriteBlockedException()
    : IOException(
        "The project is being cleaned up or has been deleted. Retry after checking the project."
    );
