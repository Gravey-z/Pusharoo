using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayEntitlementService(
    MongoDbContext db,
    IWebhookSubscriptionRepository subscriptions,
    IOptions<EventRelayOptions> options,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public RelayLifecycleService Lifecycle => new(db, options, clock);

    public Task<RelayEntitlementDocument> GetAsync(
        string projectId,
        string network,
        CancellationToken ct
    ) => Lifecycle.GetEntitlementAsync(projectId, network, ct);

    public static string PeriodId(RelayEntitlementDocument entitlement) =>
        $"{entitlement.Id}:{new DateTimeOffset(entitlement.PeriodStart.ToUniversalTime()).ToUnixTimeMilliseconds()}:{entitlement.LastPaymentId ?? "free"}";

    public Task<bool> WasChargedAsync(string periodId, string deliveryId, CancellationToken ct) =>
        Lifecycle.WasChargedAsync(periodId, deliveryId, ct);

    public Task<bool> TryConsumeEventAsync(
        RelayEntitlementDocument entitlement,
        string deliveryId,
        CancellationToken ct,
        string? subscriptionId = null
    ) => Lifecycle.TryConsumeAsync(entitlement, deliveryId, ct, subscriptionId);

    public async Task<IReadOnlyList<RelayEntitlementDocument>> GrantPaidAccessAsync(
        string projectId,
        string paymentId,
        CancellationToken ct,
        RelayPurchaseTerms? purchaseTerms = null
    )
    {
        var now = DateTimeOffset
            .FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds())
            .UtcDateTime;
        var terms =
            purchaseTerms
            ?? new RelayPurchaseTerms
            {
                Package = RelayPackageCatalog.LegacyTerms(options.Value),
                RequiredGasDatoshis = options.Value.PaidPlanGasDatoshis,
                QuotedAt = now,
                Source = "legacy_grant",
            };
        var id = $"{projectId}:{paymentId}";
        var period = await db.PurchasedPeriods.Find(x => x.Id == id).FirstOrDefaultAsync(ct);

        if (period is null)
        {
            var current = await GetAsync(projectId, "neo3:mainnet", ct);
            var old = current.PaymentGrants.FirstOrDefault(x => x.PaymentId == paymentId);
            var tail = current.PaidUntil;
            var start = old?.PeriodStart ?? (tail > now ? tail.Value : now);
            var end = old?.PeriodEndsAt ?? start.AddDays(terms.Package.Days);

            string Ledger(string network) =>
                $"{projectId}:{network}:{new DateTimeOffset(start).ToUnixTimeMilliseconds()}:{paymentId}";

            var candidate = new RelayPurchasedPeriodDocument
            {
                Id = id,
                ProjectId = projectId,
                PurchaseId = paymentId,
                StartsAt = start,
                EndsAt = end,
                OriginalTerms = terms.Package,
                EffectiveTerms = terms.Package,
                MainnetLedgerId = Ledger("neo3:mainnet"),
                Source = terms.Source,
            };

            try
            {
                await db.PurchasedPeriods.InsertOneAsync(candidate, cancellationToken: ct);
            }
            catch (MongoWriteException e)
                when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

            period = await db.PurchasedPeriods.Find(x => x.Id == id).SingleAsync(ct);
        }

        foreach (var network in new[] { "neo3:mainnet" })
        {
            var grant = new RelayEntitlementPaymentGrant
            {
                PaymentId = paymentId,
                PeriodId = period.Id,
                Terms = terms,
                PeriodStart = period.StartsAt,
                PeriodEndsAt = period.EndsAt,
                GraceEndsAt = period.EndsAt.AddDays(period.OriginalTerms.GraceDays),
            };

            await db.Entitlements.UpdateOneAsync(
                x =>
                    x.ProjectId == projectId
                    && x.Network == network
                    && !x.PaymentGrants.Any(g => g.PaymentId == paymentId),
                Builders<RelayEntitlementDocument>
                    .Update.Push(x => x.PaymentGrants, grant)
                    .Inc(x => x.Version, 1),
                cancellationToken: ct
            );

            try
            {
                await db.EntitlementHistory.UpdateOneAsync(
                    x => x.PaymentId == paymentId && x.Network == network && x.AllocationId == null,
                    Builders<RelayEntitlementHistoryDocument>
                        .Update.SetOnInsert(x => x.Id, $"{paymentId}:{network}")
                        .SetOnInsert(x => x.PaymentId, paymentId)
                        .SetOnInsert(x => x.ProjectId, projectId)
                        .SetOnInsert(x => x.Network, network)
                        .SetOnInsert(x => x.Terms, terms)
                        .SetOnInsert(x => x.PeriodId, period.Id)
                        .SetOnInsert(x => x.PeriodStart, period.StartsAt)
                        .SetOnInsert(x => x.PeriodEndsAt, period.EndsAt)
                        .SetOnInsert(x => x.GraceEndsAt, grant.GraceEndsAt)
                        .SetOnInsert(x => x.CreatedAt, now),
                    new UpdateOptions { IsUpsert = true },
                    ct
                );
            }
            catch (MongoWriteException e)
                when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                if (
                    !await db
                        .EntitlementHistory.Find(x =>
                            x.PaymentId == paymentId
                            && x.Network == network
                            && x.ProjectId == projectId
                        )
                        .AnyAsync(ct)
                )
                    throw;
            }
        }

        var result = new[] { await GetAsync(projectId, "neo3:mainnet", ct) };

        await subscriptions.SetFreeTestnetSubscriptionExpiryAsync(
            projectId,
            options.Value.TestnetSubscriptionRetentionDays,
            ct
        );

        return result;
    }

    internal async Task ClosePeriodAsync(RelayEntitlementDocument entitlement, CancellationToken ct)
    {
        await db.AllowancePeriods.UpdateOneAsync(
            x => x.Id == PeriodId(entitlement),
            Builders<RelayAllowancePeriodDocument>
                .Update.Set(x => x.Closed, true)
                .SetOnInsert(x => x.EventsUsed, entitlement.EventsUsed)
                .SetOnInsert(x => x.ChargedDeliveries, [])
                .SetOnInsert(x => x.PeriodEndsAt, entitlement.PeriodEndsAt),
            new UpdateOptions { IsUpsert = true },
            ct
        );
    }

    public async Task<IReadOnlyList<RelayEntitlementDocument>> ReconcileFreeTestnetAccessAsync(
        CancellationToken ct
    )
    {
        var projects = await db
            .Entitlements.Find(x => x.Network == "neo3:testnet")
            .Project(x => x.ProjectId)
            .ToListAsync(ct);
        var results = new List<RelayEntitlementDocument>();
        var subscriptionProjects = await db
            .Subscriptions.Find(x =>
                x.Network == "neo3:testnet" && x.ProjectId != null && !x.Deleted
            )
            .Project(x => x.ProjectId!)
            .ToListAsync(ct);

        foreach (
            var projectId in projects.Concat(subscriptionProjects).Distinct(StringComparer.Ordinal)
        )
        {
            var value = await GetAsync(projectId, "neo3:testnet", ct);

            if (value.Plan == "free_beta")
                results.Add(value);
        }

        return results;
    }
}
