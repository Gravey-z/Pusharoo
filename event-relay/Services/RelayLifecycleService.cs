using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed partial class RelayLifecycleService(
    MongoDbContext db,
    IOptions<EventRelayOptions> options,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private EventRelayOptions Settings => options.Value;

    private DateTime Now =>
        DateTimeOffset
            .FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds())
            .UtcDateTime;

    private static string Field(string network) =>
        network == "neo3:mainnet" ? "mainnet" : "testnet";

    private static RelayNetworkBudget? Budget(
        RelayProjectAdmissionDocument state,
        string network
    ) => network == "neo3:mainnet" ? state.Mainnet : state.Testnet;

    public async Task<RelayProjectAdmissionDocument> EnsureAsync(
        string projectId,
        CancellationToken ct
    )
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = await db
                .ProjectAdmissions.Find(x => x.Id == projectId)
                .Project<RelayProjectAdmissionDocument>(
                    Builders<RelayProjectAdmissionDocument>
                        .Projection.Exclude("mainnet.receipts")
                        .Exclude("testnet.receipts")
                )
                .FirstOrDefaultAsync(ct);

            if (state is null)
            {
                var initial = await db
                    .Subscriptions.Find(x =>
                        x.ProjectId == projectId
                        && x.IsEnabled
                        && !x.Deleted
                        && (x.ExpiresAt == null || x.ExpiresAt > Now)
                    )
                    .ToListAsync(ct);

                try
                {
                    await db.ProjectAdmissions.InsertOneAsync(
                        new()
                        {
                            Id = projectId,
                            Slots = initial
                                .Select(x => new RelaySubscriptionSlot(
                                    x.Id,
                                    x.Network,
                                    x.CreatedAt
                                ))
                                .ToArray(),
                        },
                        cancellationToken: ct
                    );
                }
                catch (MongoWriteException e)
                    when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

                continue;
            }

            if (state.Mutation is not null)
            {
                await ProjectMutationAsync(state, ct);
                continue;
            }

            if (state.Publication is not null)
            {
                await ProjectPublicationAsync(state, ct);
                continue;
            }

            if (state.Transition is not null)
            {
                var frozen = await db
                    .ProjectAdmissions.Find(x => x.Id == projectId && x.Transition != null)
                    .FirstOrDefaultAsync(ct);

                if (frozen is not null)
                    await FinishTransitionAsync(frozen, ct);

                continue;
            }

            var mainnet = await ReadEntitlementAsync(projectId, "neo3:mainnet", ct, state.Mainnet);
            var testnet = await ReadEntitlementAsync(projectId, "neo3:testnet", ct, state.Testnet);
            var periods = await db
                .PurchasedPeriods.Find(x => x.ProjectId == projectId)
                .ToListAsync(ct);
            var now = Now;
            var mainTarget = Desired(mainnet, periods, now);
            var testTarget = Desired(testnet, periods, now);
            var validUntil = periods
                .Where(x => x.StartsAt > now)
                .Select(x => (DateTime?)x.StartsAt)
                .Concat(
                    new[] { mainTarget, testTarget }.Select(x =>
                        x.Status == "active" ? (DateTime?)x.PeriodEndsAt
                        : x.Status == "grace" ? x.GraceEndsAt
                        : null
                    )
                )
                .Where(x => x > now)
                .Min();
            var slots = state.Slots;
            var paid =
                mainTarget.Plan == "paid" ? mainTarget
                : testTarget.Plan == "paid" ? testTarget
                : null;
            var candidates = await db
                .Subscriptions.Find(x => x.ProjectId == projectId && !x.Deleted)
                .ToListAsync(ct);
            var validIds = candidates
                .Where(x =>
                    x.ExpiresAt == null
                    || x.ExpiresAt > now
                    || paid is not null && x.Network == "neo3:testnet"
                )
                .Select(x => x.Id)
                .ToHashSet(StringComparer.Ordinal);

            slots = slots.Where(x => validIds.Contains(x.Id)).ToArray();

            if (
                paid is not null
                && state.Mainnet?.Entitlement.Plan != "paid"
                && state.Testnet?.Entitlement.Plan != "paid"
            )
                slots = slots
                    .Concat(
                        candidates
                            .Where(x =>
                                x.IsEnabled
                                && x.Network == "neo3:testnet"
                                && !slots.Any(s => s.Id == x.Id)
                            )
                            .Select(x => new RelaySubscriptionSlot(x.Id, x.Network, x.CreatedAt))
                    )
                    .ToArray();

            var paidBoundary =
                paid is not null
                && state.Mainnet?.PeriodId != RelayEntitlementService.PeriodId(mainTarget);
            var lowerBoundary =
                paidBoundary
                && paid is not null
                && (state.Mainnet?.Entitlement ?? mainnet).MaxActiveSubscriptions
                    > paid.MaxActiveSubscriptions;

            if (
                paid is not null
                && (paid.Status == "active" || paidBoundary)
                && (slots.Length > paid.MaxActiveSubscriptions || lowerBoundary)
            )
            {
                var selection = state.Selections.SingleOrDefault(x =>
                    x.PeriodId == paid.PurchasedPeriodId
                );

                slots =
                    selection is not null && lowerBoundary
                        ? slots
                            .Where(x => selection.SubscriptionIds.Contains(x.Id))
                            .Take(paid.MaxActiveSubscriptions)
                            .ToArray()
                        : slots
                            .OrderBy(x => x.CreatedAt)
                            .ThenBy(x => x.Id, StringComparer.Ordinal)
                            .Take(paid.MaxActiveSubscriptions)
                            .ToArray();
            }

            var changed =
                !Equivalent(state.Mainnet?.Entitlement, mainTarget)
                || !Equivalent(state.Testnet?.Entitlement, testTarget)
                || !Equivalent(mainnet, mainTarget)
                || !Equivalent(testnet, testTarget)
                || !state.Slots.SequenceEqual(slots)
                || state.ValidUntil != validUntil;

            if (!changed)
                return state;

            var transition = new RelayPeriodTransition
            {
                Id = Guid.NewGuid().ToString("N"),
                Revision = state.Version + 1,
                Mainnet = mainTarget,
                Testnet = testTarget,
                Slots = slots,
                AppliedAt = now,
                ValidUntil = validUntil,
                PausedIds = state
                    .Slots.Where(x => !slots.Any(y => y.Id == x.Id) && validIds.Contains(x.Id))
                    .Select(x => x.Id)
                    .ToArray(),
            };

            await db.ProjectAdmissions.UpdateOneAsync(
                x =>
                    x.Id == projectId
                    && x.Version == state.Version
                    && x.Transition == null
                    && x.Mutation == null
                    && x.Publication == null,
                Builders<RelayProjectAdmissionDocument>
                    .Update.Set(x => x.Transition, transition)
                    .Inc(x => x.Version, 1),
                cancellationToken: ct
            );
        }

        throw new IOException("Relay admission is busy; retry without resubmitting a payment.");
    }

    private async Task<RelayEntitlementDocument> ReadEntitlementAsync(
        string projectId,
        string network,
        CancellationToken ct,
        RelayNetworkBudget? fallback = null
    )
    {
        var value = await db
            .Entitlements.Find(x => x.ProjectId == projectId && x.Network == network)
            .FirstOrDefaultAsync(ct);

        if (value is not null)
            return value;

        var now = Now;

        value = fallback is not null
            ? fallback.Entitlement with
            {
                EventsUsed = fallback.EventsUsed,
            }
            : new()
            {
                Id = $"{projectId}:{network}",
                ProjectId = projectId,
                Network = network,
                PeriodStart = now,
                PeriodEndsAt = now.AddDays(Settings.FreeBetaPeriodDays),
                UpdatedAt = now,
                Status = network == "neo3:testnet" ? "active" : "expired",
                MaxEvents = network == "neo3:testnet" ? Settings.FreeTestnetMaxEvents : 0,
                MaxActiveSubscriptions =
                    network == "neo3:testnet" ? Settings.FreeTestnetMaxActiveSubscriptions : 0,
            };

        try
        {
            await db.Entitlements.InsertOneAsync(value, cancellationToken: ct);
        }
        catch (MongoWriteException e)
            when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await db.Entitlements.Find(x => x.Id == value.Id).SingleAsync(ct);
        }

        return value;
    }

    private RelayEntitlementDocument Desired(
        RelayEntitlementDocument current,
        List<RelayPurchasedPeriodDocument> periods,
        DateTime now
    )
    {
        var active = periods.Where(x => x.StartsAt <= now && x.EndsAt > now).ToArray();

        if (active.Length > 1)
            return current with { Status = "reconciliation_required" };

        var samePeriod = periods.SingleOrDefault(x => x.Id == current.PurchasedPeriodId);
        var nextPeriod = periods
            .Where(x => x.StartsAt <= now && x.StartsAt > current.PeriodStart)
            .OrderBy(x => x.StartsAt)
            .FirstOrDefault();

        var period =
            samePeriod is not null && current.PeriodEndsAt > now
                ? samePeriod
                : nextPeriod
                    ?? samePeriod
                    ?? active.SingleOrDefault()
                    ?? periods
                        .Where(x => x.StartsAt <= now)
                        .OrderByDescending(x => x.StartsAt)
                        .FirstOrDefault();

        if (
            period is not null
            && (
                current.Plan != "paid"
                || current.PeriodStart <= period.StartsAt
                || current.PurchasedPeriodId == period.Id
            )
        )
        {
            var terms = period.EffectiveTerms;

            var ledgerId =
                current.Network == "neo3:mainnet" ? period.MainnetLedgerId : period.TestnetLedgerId;
            var same =
                current.PurchasedPeriodId == period.Id
                || RelayEntitlementService.PeriodId(current) == ledgerId;

            current = current with
            {
                Plan = "paid",
                PackageId = terms.PackageId,
                PackageTerms = terms,
                PurchasedPeriodId = period.Id,
                PeriodStart = same ? current.PeriodStart : period.StartsAt,
                PeriodEndsAt =
                    same && period.Source.StartsWith("legacy", StringComparison.Ordinal)
                        ? current.PeriodEndsAt
                        : period.EndsAt,
                LastPaymentId = same ? current.LastPaymentId : period.PurchaseId,
                MaxEvents = terms.MaxEvents,
                MaxActiveSubscriptions = terms.MaxActiveSubscriptions,
                EventsUsed = same ? current.EventsUsed : 0,
                GraceEndsAt = period.EndsAt.AddDays(terms.GraceDays),
            };
        }

        var tail = periods.Select(x => (DateTime?)x.EndsAt).Append(current.PaidUntil).Max();

        if (tail is not null)
            current = current with { PaidUntil = tail };

        if (current.Plan == "free_beta" && current.Network == "neo3:testnet")
        {
            var start = current.PeriodEndsAt <= now ? now : current.PeriodStart;

            return current with
            {
                Status = "active",
                PeriodStart = start,
                PeriodEndsAt = start.AddDays(Settings.FreeBetaPeriodDays),
                MaxEvents = Settings.FreeTestnetMaxEvents,
                MaxActiveSubscriptions = Settings.FreeTestnetMaxActiveSubscriptions,
                EventsUsed = start == current.PeriodStart ? current.EventsUsed : 0,
            };
        }

        return current with
        {
            Status =
                current.PeriodStart > now ? "scheduled"
                : current.PeriodEndsAt > now
                && (current.Plan == "paid" || current.Network == "neo3:testnet")
                    ? "active"
                : current.GraceEndsAt > now ? "grace"
                : "expired",
        };
    }

    private static bool Equivalent(RelayEntitlementDocument? a, RelayEntitlementDocument b) =>
        a is not null
        && a.Plan == b.Plan
        && a.Status == b.Status
        && a.PackageId == b.PackageId
        && a.PackageTerms == b.PackageTerms
        && a.PurchasedPeriodId == b.PurchasedPeriodId
        && a.PeriodStart == b.PeriodStart
        && a.PeriodEndsAt == b.PeriodEndsAt
        && a.GraceEndsAt == b.GraceEndsAt
        && a.PaidUntil == b.PaidUntil
        && a.LastPaymentId == b.LastPaymentId
        && a.MaxEvents == b.MaxEvents
        && a.MaxActiveSubscriptions == b.MaxActiveSubscriptions;

    private async Task FinishTransitionAsync(
        RelayProjectAdmissionDocument state,
        CancellationToken ct
    )
    {
        var change = state.Transition!;

        async Task<RelayNetworkBudget> Prepare(
            RelayNetworkBudget? old,
            RelayEntitlementDocument target
        )
        {
            var id = RelayEntitlementService.PeriodId(target);

            if (old is not null && old.PeriodId != id)
                await ArchiveAsync(old, true, ct);

            var ledger =
                old?.PeriodId == id
                    ? null
                    : await db.AllowancePeriods.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            var budget = new RelayNetworkBudget
            {
                Entitlement = target,
                PeriodId = id,
                EventsUsed =
                    old?.PeriodId == id ? old.EventsUsed : ledger?.EventsUsed ?? target.EventsUsed,
                Receipts =
                    old?.PeriodId == id ? old.Receipts : ledger?.ChargedDeliveries.ToArray() ?? [],
            };

            await ProjectEntitlementAsync(
                target with
                {
                    EventsUsed = budget.EventsUsed,
                },
                change.Revision,
                ct
            );

            if (target.Plan == "paid")
                await db.Subscriptions.UpdateManyAsync(
                    x => x.ProjectId == state.Id && x.Network == "neo3:testnet" && !x.Deleted,
                    Builders<WebhookSubscriptionDocument>.Update.Set(x => x.ExpiresAt, null),
                    cancellationToken: ct
                );

            return budget;
        }

        var mainnet = await Prepare(state.Mainnet, change.Mainnet);
        var testnet = await Prepare(state.Testnet, change.Testnet);

        foreach (var id in change.PausedIds)
            await db.Subscriptions.UpdateOneAsync(
                x => x.Id == id && x.AdmissionRevision < change.Revision,
                Builders<WebhookSubscriptionDocument>
                    .Update.Set(x => x.IsEnabled, false)
                    .Set(x => x.PauseReason, "package_limit")
                    .Set(x => x.AdmissionRevision, change.Revision)
                    .Set(x => x.UpdatedAt, change.AppliedAt),
                cancellationToken: ct
            );

        await db.ProjectAdmissions.UpdateOneAsync(
            x => x.Id == state.Id && x.Transition != null && x.Transition.Id == change.Id,
            Builders<RelayProjectAdmissionDocument>
                .Update.Set(x => x.Mainnet, mainnet)
                .Set(x => x.Testnet, testnet)
                .Set(x => x.Slots, change.Slots)
                .Set(x => x.Transition, null)
                .Set(x => x.ValidUntil, change.ValidUntil)
                .Set(
                    x => x.LastTransition,
                    change.PausedIds.Length > 0 || state.LastTransition is null
                        ? new RelayTransitionOutcome(change.Id, change.AppliedAt, change.PausedIds)
                        : state.LastTransition
                )
                .Set(x => x.NextCheckAt, Now.AddSeconds(5)),
            cancellationToken: ct
        );
    }

    private async Task ProjectEntitlementAsync(
        RelayEntitlementDocument target,
        long revision,
        CancellationToken ct
    )
    {
        var filter =
            Builders<RelayEntitlementDocument>.Filter.Eq(x => x.Id, target.Id)
            & (
                Builders<RelayEntitlementDocument>.Filter.Lt(x => x.LifecycleRevision, revision)
                | Builders<RelayEntitlementDocument>.Filter.Exists(x => x.LifecycleRevision, false)
            );

        await db.Entitlements.UpdateOneAsync(
            filter,
            Builders<RelayEntitlementDocument>
                .Update.Set(x => x.Plan, target.Plan)
                .Set(x => x.Status, target.Status)
                .Set(x => x.PackageId, target.PackageId)
                .Set(x => x.PackageTerms, target.PackageTerms)
                .Set(x => x.PurchasedPeriodId, target.PurchasedPeriodId)
                .Set(x => x.PeriodStart, target.PeriodStart)
                .Set(x => x.PeriodEndsAt, target.PeriodEndsAt)
                .Set(x => x.PaidUntil, target.PaidUntil)
                .Set(x => x.GraceEndsAt, target.GraceEndsAt)
                .Set(x => x.LastPaymentId, target.LastPaymentId)
                .Set(x => x.MaxEvents, target.MaxEvents)
                .Set(x => x.MaxActiveSubscriptions, target.MaxActiveSubscriptions)
                .Set(x => x.EventsUsed, target.EventsUsed)
                .Set(x => x.LifecycleRevision, revision)
                .Set(x => x.UpdatedAt, Now)
                .Inc(x => x.Version, 1),
            cancellationToken: ct
        );
    }

    private async Task ArchiveAsync(RelayNetworkBudget budget, bool closed, CancellationToken ct)
    {
        var update = Builders<RelayAllowancePeriodDocument>
            .Update.Max(x => x.EventsUsed, budget.EventsUsed)
            .SetOnInsert(x => x.PeriodEndsAt, budget.Entitlement.PeriodEndsAt);

        if (budget.Receipts.Length > 0)
            update = update.AddToSetEach(x => x.ChargedDeliveries, budget.Receipts);
        else
            update = update.SetOnInsert(x => x.ChargedDeliveries, []);

        update = closed
            ? update.Set(x => x.Closed, true)
            : update.SetOnInsert(x => x.Closed, false);

        try
        {
            await db
                .AllowancePeriods.WithWriteConcern(WriteConcern.WMajority.With(journal: true))
                .UpdateOneAsync(
                    x => x.Id == budget.PeriodId,
                    update,
                    new UpdateOptions { IsUpsert = true },
                    ct
                );
        }
        catch (MongoWriteException e)
            when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            await db.AllowancePeriods.UpdateOneAsync(
                x => x.Id == budget.PeriodId,
                update,
                cancellationToken: ct
            );
        }
    }

    public async Task<RelayEntitlementDocument> GetEntitlementAsync(
        string projectId,
        string network,
        CancellationToken ct
    )
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var state = await EnsureAsync(projectId, ct);
            var budget = Budget(state, network)!;
            var stored = await db
                .Entitlements.Find(x => x.Id == budget.Entitlement.Id)
                .SingleAsync(ct);

            if (Equivalent(stored, budget.Entitlement))
                return stored with { EventsUsed = budget.EventsUsed };
        }

        throw new IOException("Relay period changed while reading usage; retry.");
    }
}
