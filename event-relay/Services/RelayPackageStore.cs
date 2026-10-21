using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayPackageStore(
    MongoDbContext db,
    RelayPackageCatalog catalog,
    IOptions<EventRelayOptions> options
)
{
    public async Task<RelayCatalogResponse> DescribeAsync(CancellationToken ct)
    {
        var manifest = new BsonDocument
        {
            { "_id", "active-catalog" },
            { "revision", catalog.Revision },
            { "fingerprint", catalog.Fingerprint },
        };

        try
        {
            await db.BillingSchema.InsertOneAsync(manifest, cancellationToken: ct);
        }
        catch (MongoWriteException error)
            when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

        var active = await db
            .BillingSchema.Find(new BsonDocument("_id", "active-catalog"))
            .SingleAsync(ct);
        var blocker =
            active["fingerprint"].AsString != catalog.Fingerprint
                ? "Relay catalog differs from the shared database manifest; synchronize both relays before enabling billing."
                : null;

        if (blocker is not null)
        {
            var conflict = new BsonDocument
            {
                { "_id", "catalog-conflict:" + catalog.Fingerprint },
                { "revision", catalog.Revision },
                { "fingerprint", catalog.Fingerprint },
            };

            try
            {
                await db.BillingSchema.InsertOneAsync(conflict, cancellationToken: ct);
            }
            catch (MongoWriteException error)
                when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        }

        if (
            await db
                .BillingSchema.Find(
                    new BsonDocument("_id", new BsonDocument("$regex", "^catalog-conflict:"))
                )
                .AnyAsync(ct)
        )
            blocker ??=
                "A relay reported a catalog conflict. Synchronize both relays and clear the recorded conflict through the rollout procedure before new billing.";

        var legacy = RelayPackageCatalog.LegacyTerms(options.Value);

        if (legacy.PackageId != "starter")
            blocker ??=
                "Non-default legacy paid settings require a recorded legacy baseline; they cannot redefine Starter or Pro.";

        return new(
            catalog.Revision,
            catalog.Fingerprint,
            "project_mainnet",
            "project_mainnet_per_period",
            catalog.Packages.Select(x => new RelayPackageMetadata(x, blocker is null)).ToArray(),
            blocker is null,
            blocker,
            RelayPackageCatalog.BonusPolicyVersion
        );
    }

    public Task InsertPeriodAsync(RelayPurchasedPeriodDocument period, CancellationToken ct)
    {
        if (
            string.IsNullOrWhiteSpace(period.Id)
            || string.IsNullOrWhiteSpace(period.ProjectId)
            || string.IsNullOrWhiteSpace(period.PurchaseId)
            || period.EndsAt <= period.StartsAt
            || period.Version < 0
        )
            throw new ArgumentException("Invalid purchased period identity, dates or version.");

        return InsertImmutableAsync(db.PurchasedPeriods, period.Id, period, ct);
    }

    public Task AppendUpgradeAsync(RelayPeriodUpgradeDocument upgrade, CancellationToken ct)
    {
        if (
            string.IsNullOrWhiteSpace(upgrade.Id)
            || string.IsNullOrWhiteSpace(upgrade.ProjectId)
            || string.IsNullOrWhiteSpace(upgrade.DecisionId)
            || string.IsNullOrWhiteSpace(upgrade.Target.PeriodId)
            || upgrade.Target.ExpectedVersion < 0
            || upgrade.Target.OriginalTerms.PackageId != "starter"
            || upgrade.Target.TargetTerms.PackageId != "pro"
            || upgrade.Target.EndsAt <= upgrade.Target.StartsAt
            || (
                upgrade.IsBonus
                    ? upgrade.Target.PriceDatoshis != 0
                    : upgrade.Target.PriceDatoshis <= 0
            )
        )
            throw new ArgumentException("Invalid period upgrade terms.");

        return InsertImmutableAsync(db.PeriodUpgrades, upgrade.Id, upgrade, ct);
    }

    public async Task InsertDecisionAsync(
        RelayBillingDecisionDocument decision,
        CancellationToken ct
    )
    {
        if (
            string.IsNullOrWhiteSpace(decision.Id)
            || string.IsNullOrWhiteSpace(decision.ProjectId)
            || string.IsNullOrWhiteSpace(decision.IntentId)
            || decision.Allocations.Length == 0
            || decision.Allocations.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count()
                != decision.Allocations.Length
            || decision.Allocations.Any(x =>
                string.IsNullOrWhiteSpace(x.Id)
                || string.IsNullOrWhiteSpace(x.PeriodId)
                || x.PriceDatoshis < 0
                || x.Kind is not ("purchase" or "upgrade" or "bonus")
                || x.Status is not ("accepted" or "ineligible")
                || (x.Kind == "bonus" && x.PriceDatoshis != 0)
            )
            || decision.Terms.RequiredGasDatoshis <= 0
            || decision.Allocations.Aggregate(0L, (sum, line) => checked(sum + line.PriceDatoshis))
                != decision.Terms.RequiredGasDatoshis
        )
            throw new ArgumentException("Invalid billing decision allocations.");

        await InsertImmutableAsync(db.BillingDecisions, decision.Id, decision, ct);

        foreach (var allocation in decision.Allocations.Where(x => x.Status == "accepted"))
        {
            var progress = new RelayGrantProgressDocument
            {
                Id = ProgressId(decision.Id, allocation.Id),
                DecisionId = decision.Id,
                AllocationId = allocation.Id,
            };

            await db.GrantProgress.UpdateOneAsync(
                x => x.Id == progress.Id,
                Builders<RelayGrantProgressDocument>
                    .Update.SetOnInsert(x => x.DecisionId, progress.DecisionId)
                    .SetOnInsert(x => x.AllocationId, progress.AllocationId)
                    .SetOnInsert(x => x.AppliedNetworks, [])
                    .SetOnInsert(x => x.Version, 0),
                new UpdateOptions { IsUpsert = true },
                ct
            );
        }
    }

    public async Task<bool> MarkNetworkAppliedAsync(
        string decisionId,
        string allocationId,
        string network,
        long expectedVersion,
        CancellationToken ct
    )
    {
        if (network is not ("neo3:mainnet" or "neo3:testnet"))
            throw new ArgumentException("Unsupported grant network.", nameof(network));

        var id = ProgressId(decisionId, allocationId);
        var result = await db.GrantProgress.UpdateOneAsync(
            x => x.Id == id && x.Version == expectedVersion && !x.AppliedNetworks.Contains(network),
            Builders<RelayGrantProgressDocument>
                .Update.AddToSet(x => x.AppliedNetworks, network)
                .Inc(x => x.Version, 1),
            cancellationToken: ct
        );

        return result.ModifiedCount == 1
            || await db
                .GrantProgress.Find(x => x.Id == id && x.AppliedNetworks.Contains(network))
                .AnyAsync(ct);
    }

    public async Task<IReadOnlyList<RelayPeriodResponse>> PeriodsAsync(
        string projectId,
        CancellationToken ct
    )
    {
        var periods = await db
            .PurchasedPeriods.Find(x => x.ProjectId == projectId)
            .SortBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        return periods
            .Select(x => new RelayPeriodResponse(
                x.Id,
                x.StartsAt,
                x.EndsAt,
                x.OriginalTerms,
                x.EffectiveTerms,
                x.Version,
                x.Source
            ))
            .ToArray();
    }

    public async Task<BsonDocument> InventoryAsync(CancellationToken ct)
    {
        var hello = await db.BillingSchema.Database.RunCommandAsync<BsonDocument>(
            new BsonDocument("hello", 1),
            cancellationToken: ct
        );

        return new BsonDocument
        {
            { "schemaVersion", 2 },
            {
                "topology",
                hello.Contains("setName") ? "replica_set"
                : hello.GetValue("msg", "") == "isdbgrid" ? "sharded"
                : "standalone"
            },
            { "transactionsRequired", false },
            {
                "entitlements",
                await db.Entitlements.CountDocumentsAsync(_ => true, cancellationToken: ct)
            },
            {
                "unsnapshottedIntents",
                await db.PaymentIntents.CountDocumentsAsync(
                    x => x.Terms == null,
                    cancellationToken: ct
                )
            },
            {
                "purchasedPeriods",
                await db.PurchasedPeriods.CountDocumentsAsync(_ => true, cancellationToken: ct)
            },
        };
    }

    public async Task CaptureLegacyBaselineAsync(
        string id,
        string sourceRevision,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(sourceRevision))
            throw new ArgumentException("Baseline ID and source revision are required.");

        var baseline = new BsonDocument
        {
            { "_id", "legacy:" + id },
            { "sourceRevision", sourceRevision },
            { "terms", RelayPackageCatalog.LegacyTerms(options.Value).ToBsonDocument() },
            { "schemaVersion", 2 },
        };

        await InsertImmutableAsync(db.BillingSchema, "legacy:" + id, baseline, ct);
        using var cursor = await db.Entitlements.FindAsync(
            x => x.Plan == "paid",
            cancellationToken: ct
        );

        while (await cursor.MoveNextAsync(ct))
            foreach (var entitlement in cursor.Current)
            {
                var ledgerId = RelayEntitlementService.PeriodId(entitlement);
                var ledger = await db
                    .AllowancePeriods.Find(x => x.Id == ledgerId)
                    .FirstOrDefaultAsync(ct);
                var snapshot = new BsonDocument
                {
                    { "_id", $"legacy:{id}:{entitlement.Id}" },
                    { "baselineId", id },
                    { "entitlement", entitlement.ToBsonDocument() },
                    { "ledgerId", ledgerId },
                    { "eventsUsed", ledger?.EventsUsed ?? entitlement.EventsUsed },
                    { "mapping", "legacy_carryover" },
                    { "needsReconciliation", true },
                };

                try
                {
                    await db.LegacyBillingSnapshots.InsertOneAsync(snapshot, cancellationToken: ct);
                }
                catch (MongoWriteException error)
                    when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
            }
    }

    public static string ProgressId(string decisionId, string allocationId) =>
        $"{decisionId}:{allocationId}";

    private static async Task InsertImmutableAsync<T>(
        IMongoCollection<T> collection,
        string id,
        T value,
        CancellationToken ct
    )
    {
        try
        {
            await collection.InsertOneAsync(value, cancellationToken: ct);
        }
        catch (MongoWriteException error)
            when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var stored = await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);

            if (stored is null || !stored.ToBsonDocument().Equals(value.ToBsonDocument()))
                throw new InvalidOperationException(
                    "Conflicting immutable Relay billing record; original terms were retained.",
                    error
                );
        }
    }
}

public sealed class RelayCatalogReadinessWorker(
    IServiceScopeFactory scopes,
    ILogger<RelayCatalogReadinessWorker> logger,
    IOptions<NeoRpcOptions> network
) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (network.Value.Network != "neo3:mainnet")
            return;

        using var scope = scopes.CreateScope();
        var catalog = await scope
            .ServiceProvider.GetRequiredService<RelayPackageStore>()
            .DescribeAsync(ct);

        if (!catalog.BillingReady)
            logger.LogError("Relay billing disabled: {Reason}", catalog.BillingBlocker);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
