using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public enum ReplayReservationStatus
{
    Reserved,
    Contended,
    HistoryFull,
}

public sealed class ReplayAdmissionSnapshot
{
    internal BsonValue Revision { get; }
    internal IReadOnlyList<BsonDocument> Entries { get; }
    public DateTime ServerNow { get; }
    public IReadOnlyList<ReplayAdmissionUsage> Usage { get; }

    internal ReplayAdmissionSnapshot(
        BsonValue revision,
        DateTime serverNow,
        IReadOnlyList<BsonDocument> entries
    )
    {
        Revision = revision;
        ServerNow = serverNow;
        Entries = entries;
        Usage = entries
            .Select(entry => new ReplayAdmissionUsage(
                entry["job"]["_id"].AsString,
                entry["job"]["projectId"].AsString,
                entry["blocks"].ToInt64(),
                entry["admittedAt"].ToUniversalTime(),
                entry["active"].AsBoolean
            ))
            .ToArray();
    }
}

public sealed class ReplayAdmissionRepository(MongoDbContext db)
{
    private IMongoCollection<ReplayJobDocument> Jobs =>
        db.ReplayJobs.WithWriteConcern(WriteConcern.WMajority.With(journal: true));

    public async Task<ReplayAdmissionSnapshot> ReadAsync(string network, CancellationToken ct)
    {
        var ledger = await db
            .ReplayAdmissions.Find(new BsonDocument("_id", network))
            .Project<BsonDocument>(
                new BsonDocument
                {
                    { "revision", 1 },
                    { "entries", 1 },
                    { "serverNow", "$$NOW" },
                }
            )
            .SingleAsync(ct);

        var now = ledger["serverNow"].ToUniversalTime();
        var entries = ledger["entries"].AsBsonArray.Select(x => x.AsBsonDocument).ToList();

        var activeIds = entries
            .Where(x => x["active"].AsBoolean)
            .Select(x => x["job"]["_id"].AsString)
            .ToArray();

        var actual = await Jobs.Find(Builders<ReplayJobDocument>.Filter.In(x => x.Id, activeIds))
            .ToListAsync(ct);

        var finished = actual.Where(x => !x.Active).Select(x => x.Id).ToHashSet();

        foreach (var entry in entries.Where(x => finished.Contains(x["job"]["_id"].AsString)))
            entry["active"] = false;

        entries.RemoveAll(x =>
            !x["active"].AsBoolean && x["admittedAt"].ToUniversalTime() <= now.AddDays(-1)
        );

        return new ReplayAdmissionSnapshot(ledger["revision"], now, entries);
    }

    public async Task<ReplayReservationStatus> TryReserveAsync(
        string network,
        ReplayAdmissionSnapshot snapshot,
        ReplayJobDocument job,
        CancellationToken ct
    )
    {
        var entries = snapshot
            .Entries.Append(
                new BsonDocument
                {
                    { "job", job.ToBsonDocument() },
                    { "blocks", job.TotalBlocks },
                    { "admittedAt", snapshot.ServerNow },
                    { "active", true },
                }
            )
            .ToArray();

        if (new BsonDocument("entries", new BsonArray(entries)).ToBson().Length > 8 * 1024 * 1024)
            return ReplayReservationStatus.HistoryFull;

        var changed = await db.ReplayAdmissions.UpdateOneAsync(
            new BsonDocument { { "_id", network }, { "revision", snapshot.Revision } },
            new BsonDocument
            {
                { "$set", new BsonDocument("entries", new BsonArray(entries)) },
                { "$inc", new BsonDocument("revision", 1L) },
            },
            cancellationToken: ct
        );

        return changed.ModifiedCount == 1
            ? ReplayReservationStatus.Reserved
            : ReplayReservationStatus.Contended;
    }

    public async Task EnsureAsync(string network, CancellationToken ct)
    {
        if (await db.ReplayAdmissions.Find(new BsonDocument("_id", network)).AnyAsync(ct))
            return;

        // Import existing jobs on first deployment, so an upgrade cannot reset usage.

        var hello = await db.ReplayAdmissions.Database.RunCommandAsync<BsonDocument>(
            new BsonDocument("hello", 1),
            cancellationToken: ct
        );

        var cutoff = hello["localTime"].ToUniversalTime().AddDays(-1);

        var existing = await Jobs.Find(x =>
                x.Network == network && (x.Active || x.CreatedAt > cutoff)
            )
            .Limit(1101)
            .ToListAsync(ct);

        if (existing.Count > 1100)
            throw new InvalidOperationException(
                "Existing replay history exceeds admission initialization bounds; operator review required."
            );

        var entries = existing.Select(x => new BsonDocument
        {
            { "job", x.ToBsonDocument() },
            { "blocks", x.TotalBlocks },
            { "admittedAt", x.CreatedAt },
            { "active", x.Active },
        });

        try
        {
            await db.ReplayAdmissions.InsertOneAsync(
                new BsonDocument
                {
                    { "_id", network },
                    { "revision", 0L },
                    { "entries", new BsonArray(entries) },
                },
                cancellationToken: ct
            );
        }
        catch (MongoWriteException e)
            when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    public async Task RecoverAsync(string network, CancellationToken ct)
    {
        var ledger = await db
            .ReplayAdmissions.Find(new BsonDocument("_id", network))
            .FirstOrDefaultAsync(ct);

        if (ledger is null)
            return; // Existing jobs still run before the first limited submission.

        var intents = ledger["entries"]
            .AsBsonArray.Select(x => x.AsBsonDocument)
            .Where(x => x["active"].AsBoolean)
            .ToArray();

        var ids = intents.Select(x => x["job"]["_id"].AsString).ToArray();

        var present = (
            await Jobs.Find(Builders<ReplayJobDocument>.Filter.In(x => x.Id, ids))
                .Project(x => x.Id)
                .ToListAsync(ct)
        ).ToHashSet();

        foreach (var intent in intents.Where(x => !present.Contains(x["job"]["_id"].AsString)))
            await MaterializeAsync(
                BsonSerializer.Deserialize<ReplayJobDocument>(intent["job"].AsBsonDocument),
                ct
            );
    }

    public async Task MaterializeAsync(ReplayJobDocument job, CancellationToken ct)
    {
        try
        {
            await Jobs.InsertOneAsync(job, cancellationToken: ct);
        }
        catch (MongoWriteException e)
            when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            if (await Jobs.Find(x => x.Id == job.Id).AnyAsync(ct))
                return;

            // A conflicting legacy writer must not leave an unrecoverable reserved slot.

            await db.ReplayAdmissions.UpdateOneAsync(
                new BsonDocument { { "_id", job.Network }, { "entries.job._id", job.Id } },
                new BsonDocument
                {
                    { "$set", new BsonDocument("entries.$.active", false) },
                    { "$inc", new BsonDocument("revision", 1L) },
                },
                cancellationToken: ct
            );

            throw;
        }
    }
}
