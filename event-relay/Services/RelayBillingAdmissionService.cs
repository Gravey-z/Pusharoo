using MongoDB.Driver;
using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayBillingAdmissionService(
    MongoDbContext db,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<RelayPaymentIntentDocument> ReserveAsync(
        string projectId,
        string requestKey,
        string fingerprint,
        Func<Task<RelayPaymentIntentDocument>> create,
        CancellationToken ct
    )
    {
        var prior = await db
            .PaymentIntents.Find(x => x.ProjectId == projectId && x.RequestKey == requestKey)
            .FirstOrDefaultAsync(ct);

        if (prior is not null)
            return SameRequest(prior, fingerprint);

        try
        {
            await db.BillingLedgers.InsertOneAsync(new() { Id = projectId }, cancellationToken: ct);
        }
        catch (MongoWriteException error)
            when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var ledger = await db.BillingLedgers.Find(x => x.Id == projectId).SingleAsync(ct);

            if (ledger.OpenIntent is { } reservation)
            {
                var open = await MaterializeAsync(reservation, ct);

                if (open.Id != reservation.Id)
                    await ReleaseAsync(projectId, reservation.Id, ct);

                if (open.RequestKey == requestKey)
                    return SameRequest(open, fingerprint);

                if (
                    open.SubmittedTransactionId is null
                    && open.Status == "pending"
                    && open.ExpiresAt <= Now()
                )
                {
                    await db.PaymentIntents.UpdateOneAsync(
                        x =>
                            x.Id == open.Id
                            && x.SubmittedTransactionId == null
                            && x.Status == "pending",
                        Builders<RelayPaymentIntentDocument>.Update.Set(x => x.Status, "expired"),
                        cancellationToken: ct
                    );
                    open = await db.PaymentIntents.Find(x => x.Id == open.Id).SingleAsync(ct);
                }

                if (
                    open.Status is "confirmed" or "rejected" or "resolved"
                    || open.Status is "expired" or "cancelled"
                        && open.SubmittedTransactionId is null
                )
                {
                    await ReleaseAsync(projectId, open.Id, ct);
                    continue;
                }

                throw new PaymentValidationException(
                    $"Resolve existing payment intent {open.Id} ({open.Status}) before another quote.",
                    409
                );
            }

            var now = Now();
            var legacyOpen = await db
                .PaymentIntents.Find(x =>
                    x.ProjectId == projectId
                    && (
                        x.Status == "applying"
                        || x.Status == "reconciliation_required"
                        || (x.Status == "expired" && x.SubmittedTransactionId != null)
                        || (
                            (x.Status == "pending" || x.Status == "awaiting_finality")
                            && (x.SubmittedTransactionId != null || x.ExpiresAt > now)
                        )
                    )
                )
                .SortBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            var candidate = legacyOpen ?? await create();
            var result = await db.BillingLedgers.UpdateOneAsync(
                x => x.Id == projectId && x.Version == ledger.Version && x.OpenIntent == null,
                Builders<RelayBillingLedgerDocument>
                    .Update.Set(x => x.OpenIntent, candidate)
                    .Inc(x => x.Version, 1),
                cancellationToken: ct
            );

            if (result.ModifiedCount == 0)
                continue;

            var stored = await MaterializeAsync(candidate, ct);

            if (stored.Id != candidate.Id)
                await ReleaseAsync(projectId, candidate.Id, ct);

            if (legacyOpen is null || stored.RequestKey == requestKey)
                return SameRequest(stored, fingerprint);
        }

        throw new PaymentValidationException(
            "Billing state changed concurrently; retry this request key.",
            409
        );
    }

    public Task ReleaseAsync(string projectId, string intentId, CancellationToken ct) =>
        db.BillingLedgers.UpdateOneAsync(
            x => x.Id == projectId && x.OpenIntent != null && x.OpenIntent.Id == intentId,
            Builders<RelayBillingLedgerDocument>
                .Update.Set(x => x.OpenIntent, null)
                .Inc(x => x.Version, 1),
            cancellationToken: ct
        );

    private async Task<RelayPaymentIntentDocument> MaterializeAsync(
        RelayPaymentIntentDocument intent,
        CancellationToken ct
    )
    {
        try
        {
            await db.PaymentIntents.InsertOneAsync(intent, cancellationToken: ct);
        }
        catch (MongoWriteException error)
            when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

        return await db.PaymentIntents.Find(x => x.Id == intent.Id).FirstOrDefaultAsync(ct)
            ?? await db
                .PaymentIntents.Find(x =>
                    x.ProjectId == intent.ProjectId && x.RequestKey == intent.RequestKey
                )
                .SingleAsync(ct);
    }

    private static RelayPaymentIntentDocument SameRequest(
        RelayPaymentIntentDocument value,
        string fingerprint
    ) =>
        value.RequestFingerprint == fingerprint
            ? value
            : throw new PaymentValidationException(
                "Request key was already used with different terms or payer.",
                409
            );

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
