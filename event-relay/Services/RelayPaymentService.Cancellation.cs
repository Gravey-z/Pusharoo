using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public sealed partial class RelayPaymentService
{
    public async Task<PaymentIntentResponse> CancelIntentAsync(
        string projectId,
        string intentId,
        CancellationToken ct
    )
    {
        if (!IsMainNet)
            throw new PaymentValidationException("Payments are available on Mainnet only.", 404);

        var intent =
            await db
                .PaymentIntents.Find(x => x.Id == intentId && x.ProjectId == projectId)
                .FirstOrDefaultAsync(ct)
            ?? throw new PaymentValidationException("Payment quote was not found.", 404);

        var claimed = await db
            .PaymentTransactions.Find(
                new BsonDocument { { "intentId", intent.Id }, { "kind", "payment" } }
            )
            .AnyAsync(ct);

        var verified = await db.Payments.Find(x => x.IntentId == intent.Id).AnyAsync(ct);

        if (claimed || verified)
            throw new PaymentValidationException(
                "A payment has already been submitted. Check its confirmation instead of cancelling.",
                409
            );

        if (intent.Status == "cancelled" && intent.SubmittedTransactionId is null)
        {
            await Admission.ReleaseAsync(projectId, intent.Id, ct);
            return ToResponse(intent);
        }

        await db.PaymentIntents.UpdateOneAsync(
            x =>
                x.Id == intent.Id
                && x.ProjectId == projectId
                && x.Status == "pending"
                && x.SubmittedTransactionId == null
                && x.ConfirmedTransactionId == null
                && x.ApplicationDecisionId == null,
            Builders<RelayPaymentIntentDocument>
                .Update.Set(x => x.Status, "cancelled")
                .Set(x => x.CancelledAt, Now())
                .Set(x => x.RecoveryMessage, "Quote cancelled. You can choose another package."),
            cancellationToken: ct
        );

        intent = await IntentAsync(intent.Id, ct);
        if (intent.Status != "cancelled" || intent.SubmittedTransactionId is not null)
            throw new PaymentValidationException(
                "This quote can no longer be cancelled. Refresh billing and check its payment status.",
                409
            );

        await Admission.ReleaseAsync(projectId, intent.Id, ct);
        return ToResponse(intent);
    }

    private async Task<RelayPaymentIntentDocument> BindSubmissionAsync(
        string intentId,
        string transactionId,
        DateTime submittedAt,
        RelayPaymentDocument? payment,
        CancellationToken ct
    )
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var intent = await IntentAsync(intentId, ct);
            if (intent.SubmittedTransactionId is { } existing)
                return existing == transactionId
                    ? intent
                    : throw new PaymentValidationException(
                        "This intent is bound to another transaction.",
                        409
                    );

            if (intent.Status is not ("pending" or "awaiting_finality" or "expired" or "cancelled"))
                throw new PaymentValidationException(
                    "This quote cannot accept another transaction.",
                    409
                );

            var cancelled = intent.CancelledAt is not null || intent.Status == "cancelled";
            var late =
                cancelled
                || submittedAt >= intent.ExpiresAt
                    && (payment is null || payment.VerifiedAt >= intent.ExpiresAt);
            var result = await db.PaymentIntents.UpdateOneAsync(
                x =>
                    x.Id == intent.Id
                    && x.SubmittedTransactionId == null
                    && x.Status == intent.Status
                    && x.CancelledAt == intent.CancelledAt,
                Builders<RelayPaymentIntentDocument>
                    .Update.Set(x => x.SubmittedTransactionId, transactionId)
                    .Set(x => x.SubmittedAt, submittedAt)
                    .Set(x => x.LateSubmission, late)
                    .Set(x => x.Status, "pending"),
                cancellationToken: ct
            );

            if (result.ModifiedCount != 0)
                return await IntentAsync(intent.Id, ct);
        }

        throw new PaymentValidationException(
            "Payment state changed. Retry this transaction without paying again.",
            409
        );
    }
}
