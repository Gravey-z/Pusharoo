using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public sealed record RelayPaymentApplicationOutcome(
    RelayBillingDecisionDocument Decision,
    IReadOnlyList<RelayGrantProgressDocument> Progress,
    long UnappliedGasDatoshis,
    bool Complete,
    bool Conflict,
    string? Message
);

public sealed class RelayPaymentApplicationService(
    MongoDbContext db,
    RelayPackageStore store,
    IRelayPaymentProjector projector,
    RelayLifecycleService? lifecycle = null
)
{
    private static readonly string[] Networks = ["neo3:mainnet"];

    public async Task<RelayPaymentApplicationOutcome> ApplyAsync(
        RelayPaymentIntentDocument intent,
        RelayPaymentDocument payment,
        CancellationToken ct
    )
    {
        var decision = await db
            .BillingDecisions.Find(x => x.Id == payment.Id)
            .FirstOrDefaultAsync(ct);

        if (decision is null)
        {
            var candidate = await BuildDecisionAsync(intent, payment, ct);

            try
            {
                await db.BillingDecisions.InsertOneAsync(candidate, cancellationToken: ct);
            }
            catch (MongoWriteException error)
                when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

            decision = await db.BillingDecisions.Find(x => x.Id == payment.Id).SingleAsync(ct);
        }

        if (decision.ProjectId != intent.ProjectId || decision.IntentId != intent.Id)
            throw new PaymentValidationException(
                "Payment decision belongs to another intent.",
                409
            );

        await store.InsertDecisionAsync(decision, ct);
        var unapplied = checked(
            Math.Max(0, payment.GasDatoshis - decision.Terms.RequiredGasDatoshis)
            + decision.Allocations.Where(x => x.Status == "ineligible").Sum(x => x.PriceDatoshis)
        );

        try
        {
            foreach (var allocation in decision.Allocations.Where(x => x.Status == "accepted"))
            {
                if (lifecycle is null)
                    await PreparePeriodAsync(db, store, decision, allocation, ct);
                else
                    await lifecycle.PublishPeriodAsync(decision, allocation, ct);

                foreach (var network in Networks)
                {
                    var progressId = RelayPackageStore.ProgressId(decision.Id, allocation.Id);
                    var progress = await db
                        .GrantProgress.Find(x => x.Id == progressId)
                        .SingleAsync(ct);

                    if (progress.AppliedNetworks.Contains(network))
                        continue;

                    await projector.ApplyAsync(decision, allocation, network, ct);

                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        if (
                            await store.MarkNetworkAppliedAsync(
                                decision.Id,
                                allocation.Id,
                                network,
                                progress.Version,
                                ct
                            )
                        )
                            break;

                        progress = await db
                            .GrantProgress.Find(x => x.Id == progressId)
                            .SingleAsync(ct);

                        if (attempt == 19)
                            throw new IOException("Grant progress contention.");
                    }
                }
            }
        }
        catch (Exception error)
            when (error is RelayPaymentProjectionConflictException or InvalidOperationException)
        {
            return new(
                decision,
                await ProgressAsync(decision.Id, ct),
                unapplied,
                false,
                true,
                "Payment is verified; a stored period/ledger conflict requires reconciliation. Do not send another payment."
            );
        }
        catch (Exception error) when (error is MongoException or IOException)
        {
            return new(
                decision,
                await ProgressAsync(decision.Id, ct),
                unapplied,
                false,
                false,
                "Payment is verified; grant recovery is pending. Retry confirmation without another transfer."
            );
        }

        return new(
            decision,
            await ProgressAsync(decision.Id, ct),
            unapplied,
            true,
            false,
            unapplied > 0
                ? "Applied eligible benefits; unapplied GAS is recorded as refund owed or available for an owner-approved replacement."
                : null
        );
    }

    public async Task<RelayBillingDecisionDocument> BuildDecisionAsync(
        RelayPaymentIntentDocument intent,
        RelayPaymentDocument payment,
        CancellationToken ct
    )
    {
        var terms =
            payment.Terms
            ?? throw new PaymentValidationException("Verified purchase terms are missing.", 503);
        var accepted = payment.VerifiedAt;
        var lines = new List<RelayGrantAllocation>();

        if (terms.Operation == "purchase_period")
        {
            var tail = terms.ScheduleTail;
            var existingPeriod = await db
                .PurchasedPeriods.Find(x =>
                    x.ProjectId == intent.ProjectId && x.PurchaseId == payment.Id
                )
                .FirstOrDefaultAsync(ct);
            var oldEntitlements =
                terms.Source == "catalog"
                    ? []
                    : await db
                        .Entitlements.Find(x =>
                            x.ProjectId == intent.ProjectId
                            && x.Network == "neo3:mainnet"
                            && x.Plan == "paid"
                        )
                        .ToListAsync(ct);
            var oldGrant = oldEntitlements
                .OrderBy(x => x.Network == "neo3:mainnet" ? 0 : 1)
                .SelectMany(x => x.PaymentGrants)
                .FirstOrDefault(x => x.PaymentId == payment.Id);

            if (tail is null && oldGrant is null && terms.Source != "catalog")
                tail = oldEntitlements.Select(x => x.PaidUntil).Max();

            var start =
                existingPeriod?.StartsAt
                ?? oldGrant?.PeriodStart
                ?? (tail is { } end && end > accepted ? end : accepted);
            var finish =
                existingPeriod?.EndsAt
                ?? oldGrant?.PeriodEndsAt
                ?? start.AddDays(terms.Package.Days);

            lines.Add(
                new()
                {
                    Id = "purchase",
                    PeriodId = $"{intent.ProjectId}:{payment.Id}",
                    Terms = terms.Package,
                    PriceDatoshis = terms.RequiredGasDatoshis,
                    StartsAt = start,
                    EndsAt = finish,
                    Status = intent.LateSubmission ? "ineligible" : "accepted",
                    Reason = intent.LateSubmission ? "late_submission" : null,
                }
            );

            if (terms.Bonus is { } bonus)
                lines.Add(
                    await UpgradeLineAsync("bonus", "bonus", bonus.Target, intent, accepted, ct)
                );
        }
        else if (terms.Operation == "upgrade_periods")
            foreach (var target in terms.Targets)
                lines.Add(
                    await UpgradeLineAsync(
                        "upgrade:" + target.PeriodId,
                        "upgrade",
                        target,
                        intent,
                        accepted,
                        ct
                    )
                );
        else
            throw new PaymentValidationException("Unsupported stored payment operation.", 409);

        return new()
        {
            Id = payment.Id,
            ProjectId = intent.ProjectId,
            IntentId = intent.Id,
            Terms = terms,
            AcceptedAt = accepted,
            Allocations = lines.ToArray(),
        };
    }

    private async Task<RelayGrantAllocation> UpgradeLineAsync(
        string id,
        string kind,
        RelayPeriodTarget target,
        RelayPaymentIntentDocument intent,
        DateTime accepted,
        CancellationToken ct
    )
    {
        var period = await db
            .PurchasedPeriods.Find(x => x.Id == target.PeriodId && x.ProjectId == intent.ProjectId)
            .FirstOrDefaultAsync(ct);
        var eligible =
            !intent.LateSubmission
            && period is not null
            && period.Version == target.ExpectedVersion
            && period.EffectiveTerms.PackageId == "starter"
            && period.EndsAt > accepted
            && period.StartsAt == target.StartsAt
            && period.EndsAt == target.EndsAt
            && (
                kind != "bonus"
                || period.StartsAt <= intent.CreatedAt && period.StartsAt <= accepted
            );

        return new()
        {
            Id = id,
            Kind = kind,
            PeriodId = target.PeriodId,
            PriceDatoshis = target.PriceDatoshis,
            Terms = target.TargetTerms,
            StartsAt = target.StartsAt,
            EndsAt = target.EndsAt,
            Target = target,
            Status = eligible ? "accepted" : "ineligible",
            Reason =
                eligible ? null
                : intent.LateSubmission ? "late_submission"
                : "target_expired_or_changed",
        };
    }

    internal static async Task PreparePeriodAsync(
        MongoDbContext db,
        RelayPackageStore store,
        RelayBillingDecisionDocument decision,
        RelayGrantAllocation allocation,
        CancellationToken ct
    )
    {
        if (allocation.Kind == "purchase")
        {
            var existing = await db
                .PurchasedPeriods.Find(x => x.Id == allocation.PeriodId)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                if (
                    existing.ProjectId != decision.ProjectId
                    || existing.PurchaseId != decision.Id
                    || existing.OriginalTerms != allocation.Terms
                    || existing.StartsAt != allocation.StartsAt
                    || existing.EndsAt != allocation.EndsAt
                )
                    throw new RelayPaymentProjectionConflictException(
                        "Existing purchased period conflicts with accepted terms."
                    );

                return;
            }

            string Ledger(string network) =>
                $"{decision.ProjectId}:{network}:{new DateTimeOffset(allocation.StartsAt).ToUnixTimeMilliseconds()}:{decision.Id}";

            var previous = await db
                .Entitlements.Find(x => x.ProjectId == decision.ProjectId)
                .ToListAsync(ct);

            string OriginalLedger(string network)
            {
                var entitlement = previous.FirstOrDefault(x => x.Network == network);
                var grant = entitlement?.PaymentGrants.FirstOrDefault(x =>
                    x.PaymentId == decision.Id
                );

                return grant is null
                    ? Ledger(network)
                    : RelayEntitlementService.PeriodId(
                        entitlement! with
                        {
                            PeriodStart = grant.PeriodStart,
                            LastPaymentId = decision.Id,
                        }
                    );
            }

            await store.InsertPeriodAsync(
                new()
                {
                    Id = allocation.PeriodId,
                    ProjectId = decision.ProjectId,
                    PurchaseId = decision.Id,
                    StartsAt = allocation.StartsAt,
                    EndsAt = allocation.EndsAt,
                    OriginalTerms = allocation.Terms,
                    EffectiveTerms = allocation.Terms,
                    MainnetLedgerId = OriginalLedger("neo3:mainnet"),
                },
                ct
            );

            return;
        }

        var target = allocation.Target!;
        var upgradeId = $"{decision.Id}:{allocation.Id}";
        var period = await db
            .PurchasedPeriods.Find(x =>
                x.Id == allocation.PeriodId && x.ProjectId == decision.ProjectId
            )
            .FirstOrDefaultAsync(ct);

        if (period is null)
            throw new RelayPaymentProjectionConflictException("Accepted period is missing.");

        if (period.LastUpgradeId != upgradeId)
        {
            var result = await db.PurchasedPeriods.UpdateOneAsync(
                x =>
                    x.Id == target.PeriodId
                    && x.ProjectId == decision.ProjectId
                    && x.Version == target.ExpectedVersion
                    && x.EffectiveTerms.PackageId == "starter",
                Builders<RelayPurchasedPeriodDocument>
                    .Update.Set(x => x.EffectiveTerms, allocation.Terms)
                    .Set(x => x.LastUpgradeId, upgradeId)
                    .Inc(x => x.Version, 1),
                cancellationToken: ct
            );

            if (
                result.ModifiedCount == 0
                && !(
                    await db
                        .PurchasedPeriods.Find(x =>
                            x.Id == target.PeriodId && x.LastUpgradeId == upgradeId
                        )
                        .AnyAsync(ct)
                )
            )
                throw new RelayPaymentProjectionConflictException(
                    "Accepted period version changed before projection."
                );
        }

        await store.AppendUpgradeAsync(
            new()
            {
                Id = upgradeId,
                ProjectId = decision.ProjectId,
                DecisionId = decision.Id,
                Target = target,
                IsBonus = allocation.Kind == "bonus",
                AcceptedAt = decision.AcceptedAt,
            },
            ct
        );
    }

    public async Task<IReadOnlyList<RelayGrantProgressDocument>> ProgressAsync(
        string decisionId,
        CancellationToken ct
    ) => await db.GrantProgress.Find(x => x.DecisionId == decisionId).ToListAsync(ct);
}
