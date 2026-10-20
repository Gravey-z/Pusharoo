using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public interface IRelayPaymentProjector
{
    Task ApplyAsync(
        RelayBillingDecisionDocument decision,
        RelayGrantAllocation allocation,
        string network,
        CancellationToken ct
    );
}

public sealed class RelayPaymentProjectionConflictException(string message) : Exception(message);

public sealed class RelayPaymentProjector(
    MongoDbContext db,
    RelayEntitlementService entitlements,
    IWebhookSubscriptionRepository subscriptions,
    TimeProvider? timeProvider = null
) : IRelayPaymentProjector
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task ApplyAsync(
        RelayBillingDecisionDocument decision,
        RelayGrantAllocation allocation,
        string network,
        CancellationToken ct
    )
    {
        var marker = $"{decision.Id}:{allocation.Id}";

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var current = await entitlements.GetAsync(decision.ProjectId, network, ct);

            if (current.AppliedPaymentAllocations.Contains(marker))
            {
                await HistoryAsync(decision, allocation, network, ct);

                if (network == "neo3:testnet")
                    await subscriptions.ClearTestnetSubscriptionExpiryAsync(decision.ProjectId, ct);

                return;
            }

            var updated = current with
            {
                Version = current.Version + 1,
                UpdatedAt = clock.GetUtcNow().UtcDateTime,
                AppliedPaymentAllocations = [.. current.AppliedPaymentAllocations, marker],
            };

            if (allocation.Kind == "purchase")
            {
                var grant = new RelayEntitlementPaymentGrant
                {
                    PaymentId = decision.Id,
                    PeriodId = allocation.PeriodId,
                    Terms = decision.Terms,
                    PeriodStart = allocation.StartsAt,
                    PeriodEndsAt = allocation.EndsAt,
                    GraceEndsAt = allocation.EndsAt.AddDays(allocation.Terms.GraceDays),
                };

                updated = updated with
                {
                    PaidUntil =
                        current.PaidUntil is { } end && end > allocation.EndsAt
                            ? end
                            : allocation.EndsAt,
                    PaymentGrants = current.PaymentGrants.Any(x => x.PaymentId == decision.Id)
                        ? current.PaymentGrants
                        : [.. current.PaymentGrants, grant],
                };

                if (
                    allocation.StartsAt <= clock.GetUtcNow().UtcDateTime
                    && (
                        current.Plan != "paid"
                        || current.PeriodEndsAt <= allocation.StartsAt
                        || current.LastPaymentId == decision.Id
                    )
                )
                {
                    if (current.Plan == "paid" && current.LastPaymentId == decision.Id)
                        updated = updated with
                        {
                            PurchasedPeriodId = allocation.PeriodId,
                            PackageId = allocation.Terms.PackageId,
                            PackageTerms = allocation.Terms,
                        };
                    else
                    {
                        await entitlements.ClosePeriodAsync(current, ct);
                        updated = updated with
                        {
                            Plan = "paid",
                            PackageId = allocation.Terms.PackageId,
                            PackageTerms = allocation.Terms,
                            PurchasedPeriodId = allocation.PeriodId,
                            PeriodStart = allocation.StartsAt,
                            PeriodEndsAt = allocation.EndsAt,
                            GraceEndsAt = grant.GraceEndsAt,
                            MaxEvents = allocation.Terms.MaxEvents,
                            MaxActiveSubscriptions = allocation.Terms.MaxActiveSubscriptions,
                            EventsUsed = 0,
                            LastPaymentId = decision.Id,
                            Status =
                                allocation.EndsAt > clock.GetUtcNow().UtcDateTime
                                    ? "active"
                                    : "expired",
                        };
                    }
                }
            }
            else
            {
                var target =
                    allocation.Target
                    ?? throw new RelayPaymentProjectionConflictException(
                        "Upgrade target is missing."
                    );
                var period = await db
                    .PurchasedPeriods.Find(x => x.Id == allocation.PeriodId)
                    .SingleAsync(ct);
                var ledgerId =
                    network == "neo3:mainnet" ? period.MainnetLedgerId : period.TestnetLedgerId;

                if (
                    current.PurchasedPeriodId == allocation.PeriodId
                    || RelayEntitlementService.PeriodId(current) == ledgerId
                )
                    updated = updated with
                    {
                        PackageId = allocation.Terms.PackageId,
                        PackageTerms = allocation.Terms,
                        MaxEvents = allocation.Terms.MaxEvents,
                        MaxActiveSubscriptions = allocation.Terms.MaxActiveSubscriptions,
                    };
                else if (
                    target.StartsAt <= decision.AcceptedAt
                    && allocation.EndsAt > clock.GetUtcNow().UtcDateTime
                )
                {
                    if (current.PeriodEndsAt > target.StartsAt)
                        throw new RelayPaymentProjectionConflictException(
                            "Active upgrade cannot be mapped to its original ledger."
                        );

                    await entitlements.ClosePeriodAsync(current, ct);
                    var ledger = ledgerId is null
                        ? null
                        : await db
                            .AllowancePeriods.Find(x => x.Id == ledgerId)
                            .FirstOrDefaultAsync(ct);

                    updated = updated with
                    {
                        Plan = "paid",
                        PackageId = allocation.Terms.PackageId,
                        PackageTerms = allocation.Terms,
                        PurchasedPeriodId = period.Id,
                        PeriodStart = period.StartsAt,
                        PeriodEndsAt = period.EndsAt,
                        GraceEndsAt = period.EndsAt.AddDays(allocation.Terms.GraceDays),
                        Status = "active",
                        LastPaymentId = period.PurchaseId,
                        MaxEvents = allocation.Terms.MaxEvents,
                        MaxActiveSubscriptions = allocation.Terms.MaxActiveSubscriptions,
                        EventsUsed = ledger?.EventsUsed ?? 0,
                    };
                }
            }

            var filter =
                Builders<RelayEntitlementDocument>.Filter.Eq(x => x.Id, current.Id)
                & (
                    current.Version == 0
                        ? Builders<RelayEntitlementDocument>.Filter.Or(
                            Builders<RelayEntitlementDocument>.Filter.Eq(x => x.Version, 0),
                            Builders<RelayEntitlementDocument>.Filter.Exists(x => x.Version, false)
                        )
                        : Builders<RelayEntitlementDocument>.Filter.Eq(
                            x => x.Version,
                            current.Version
                        )
                );

            if (
                (
                    await db.Entitlements.ReplaceOneAsync(filter, updated, cancellationToken: ct)
                ).ModifiedCount == 0
            )
                continue;

            await HistoryAsync(decision, allocation, network, ct);

            if (network == "neo3:testnet")
                await subscriptions.ClearTestnetSubscriptionExpiryAsync(decision.ProjectId, ct);

            return;
        }

        throw new IOException("Entitlement projection contention; retry the accepted payment.");
    }

    private async Task HistoryAsync(
        RelayBillingDecisionDocument decision,
        RelayGrantAllocation allocation,
        string network,
        CancellationToken ct
    )
    {
        if (allocation.Kind == "purchase")
        {
            var legacy = await db
                .EntitlementHistory.Find(x =>
                    x.PaymentId == decision.Id && x.Network == network && x.AllocationId == null
                )
                .FirstOrDefaultAsync(ct);

            if (legacy is not null)
            {
                var metadata = Builders<RelayEntitlementHistoryDocument>
                    .Update.Set(x => x.AllocationId, allocation.Id)
                    .Set(x => x.PeriodId, allocation.PeriodId);

                if (legacy.Terms is null)
                    metadata = metadata.Set(x => x.Terms, decision.Terms);

                await db.EntitlementHistory.UpdateOneAsync(
                    x => x.Id == legacy.Id && x.AllocationId == null,
                    metadata,
                    cancellationToken: ct
                );

                return;
            }
        }

        var row = new RelayEntitlementHistoryDocument
        {
            Id = $"{decision.Id}:{allocation.Id}:{network}",
            PaymentId = decision.Id,
            AllocationId = allocation.Id,
            ProjectId = decision.ProjectId,
            Network = network,
            Terms = decision.Terms,
            PeriodId = allocation.PeriodId,
            PeriodStart = allocation.StartsAt,
            PeriodEndsAt = allocation.EndsAt,
            GraceEndsAt = allocation.EndsAt.AddDays(allocation.Terms.GraceDays),
            CreatedAt = decision.AcceptedAt,
        };

        try
        {
            await db.EntitlementHistory.InsertOneAsync(row, cancellationToken: ct);
        }
        catch (MongoWriteException error)
            when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }
}
