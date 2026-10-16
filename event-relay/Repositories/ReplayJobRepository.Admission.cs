using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Repositories;

public sealed record ReplayAdmissionResult(
    bool Accepted,
    int StatusCode = 202,
    string? Error = null,
    DateTime? RetryAt = null
);

public sealed partial class ReplayJobRepository
{
    // One revision-fenced document per network atomically admits both quota and
    // capacity on standalone MongoDB. Full job intents survive a crash before insert.
    public async Task<ReplayAdmissionResult> TryCreateLimitedAsync(
        ReplayJobDocument job,
        EventRelayOptions settings,
        CancellationToken ct
    )
    {
        if (job.ToBlock < job.FromBlock || job.TotalBlocks > settings.ReplayMaxRangeBlocks)
            return new(false, 400, "Replay range exceeds the configured limit.");

        await EnsureAdmissionsAsync(job.Network, ct);

        await RecoverAdmissionsAsync(job.Network, ct);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var ledger = await db
                .ReplayAdmissions.Find(new BsonDocument("_id", job.Network))
                .Project<BsonDocument>(
                    new BsonDocument
                    {
                        { "revision", 1 },
                        { "entries", 1 },
                        { "serverNow", "$$NOW" }
                    }
                )
                .SingleAsync(ct);

            var now = ledger["serverNow"].ToUniversalTime();
            var entries = ledger["entries"].AsBsonArray.Select(x => x.AsBsonDocument).ToList();

            var activeIds = entries
                .Where(x => x["active"].AsBoolean)
                .Select(x => x["job"]["_id"].AsString)
                .ToArray();

            var actual = await Jobs.Find(
                    Builders<ReplayJobDocument>.Filter.In(x => x.Id, activeIds)
                )
                .ToListAsync(ct);

            var finished = actual.Where(x => !x.Active).Select(x => x.Id).ToHashSet();

            foreach (var entry in entries.Where(x => finished.Contains(x["job"]["_id"].AsString)))
                entry["active"] = false;

            entries.RemoveAll(x =>
                !x["active"].AsBoolean && x["admittedAt"].ToUniversalTime() <= now.AddDays(-1)
            );

            var project = entries
                .Where(x => x["job"]["projectId"].AsString == job.ProjectId)
                .ToArray();

            if (project.Any(x => x["active"].AsBoolean))
                return new(
                    false,
                    409,
                    "This project already has an active replay on this network. Complete or cancel it before starting another."
                );

            var recent = project
                .Where(x => x["admittedAt"].ToUniversalTime() > now.AddDays(-1))
                .OrderBy(x => x["admittedAt"].ToUniversalTime())
                .ToArray();

            if (recent.Length > 0)
            {
                var next = recent[^1]
                    ["admittedAt"]
                    .ToUniversalTime()
                    .AddSeconds(settings.ReplayCooldownSeconds);

                if (next > now)
                    return new(
                        false,
                        429,
                        $"Replay cooldown: try again after {next:yyyy-MM-dd HH:mm:ss} UTC.",
                        next
                    );
            }

            var used = recent.Sum(x => x["blocks"].ToInt64());

            if (used + job.TotalBlocks > settings.ReplayProjectBlocksPerDay)
            {
                var retry = now.AddDays(1);

                foreach (var entry in recent)
                {
                    used -= entry["blocks"].ToInt64();

                    retry = entry["admittedAt"].ToUniversalTime().AddDays(1);

                    if (used + job.TotalBlocks <= settings.ReplayProjectBlocksPerDay)
                        break;
                }

                return new(
                    false,
                    429,
                    $"Project replay quota is {settings.ReplayProjectBlocksPerDay:N0} requested blocks per rolling 24 hours on this network. Try this range again after {retry:yyyy-MM-dd HH:mm:ss} UTC. Cancelling does not refund quota.",
                    retry
                );
            }

            if (entries.Count(x => x["active"].AsBoolean) >= settings.ReplayMaxActiveJobsPerNetwork)
                return new(
                    false,
                    429,
                    "The network replay queue is full. Try again in a minute.",
                    now.AddMinutes(1)
                );

            var daily = entries
                .Where(x => x["admittedAt"].ToUniversalTime() > now.AddDays(-1))
                .ToArray();

            if (daily.Length >= settings.ReplayMaxJobsPerDayPerNetwork)
            {
                var retry = daily.Min(x => x["admittedAt"].ToUniversalTime()).AddDays(1);

                return new(
                    false,
                    429,
                    $"The network daily replay-job limit has been reached. Try again after {retry:yyyy-MM-dd HH:mm:ss} UTC.",
                    retry
                );
            }

            entries.Add(
                new BsonDocument
                {
                    { "job", job.ToBsonDocument() },
                    { "blocks", job.TotalBlocks },
                    { "admittedAt", now },
                    { "active", true }
                }
            );

            if (
                new BsonDocument("entries", new BsonArray(entries)).ToBson().Length
                > 8 * 1024 * 1024
            )
                return new(
                    false,
                    429,
                    "The network recovery history is at capacity. Try again later as older reservations expire.",
                    now.AddMinutes(1)
                );

            var changed = await db.ReplayAdmissions.UpdateOneAsync(
                new BsonDocument { { "_id", job.Network }, { "revision", ledger["revision"] } },
                new BsonDocument
                {
                    { "$set", new BsonDocument("entries", new BsonArray(entries)) },
                    { "$inc", new BsonDocument("revision", 1L) }
                },
                cancellationToken: ct
            );

            if (changed.ModifiedCount != 1)
                continue;

            // If this insert is interrupted, the replay worker recovers the durable intent.

            await MaterializeAdmissionAsync(job, ct);

            return new(true);
        }

        return new(false, 429, "Replay requests are busy. Try again shortly.");
    }

    private async Task EnsureAdmissionsAsync(string network, CancellationToken ct)
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
            { "active", x.Active }
        });

        try
        {
            await db.ReplayAdmissions.InsertOneAsync(
                new BsonDocument
                {
                    { "_id", network },
                    { "revision", 0L },
                    { "entries", new BsonArray(entries) }
                },
                cancellationToken: ct
            );
        }
        catch (MongoWriteException e)
            when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    private async Task RecoverAdmissionsAsync(string network, CancellationToken ct)
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
            await MaterializeAdmissionAsync(
                BsonSerializer.Deserialize<ReplayJobDocument>(intent["job"].AsBsonDocument),
                ct
            );
    }

    private async Task MaterializeAdmissionAsync(ReplayJobDocument job, CancellationToken ct)
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
                    { "$inc", new BsonDocument("revision", 1L) }
                },
                cancellationToken: ct
            );

            throw;
        }
    }
}
