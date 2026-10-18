using Microsoft.Extensions.Options;
using MongoDB.Driver;
using static Pusharoo.EventRelay.Services.NeoPaymentEncoding;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayPaymentService(
    MongoDbContext db,
    RelayPaymentVerifier verifier,
    RelayEntitlementService entitlements,
    IOptions<EventRelayOptions> relayOptions,
    IOptions<NeoRpcOptions> neoRpcOptions
)
{
    private readonly EventRelayOptions settings = relayOptions.Value;

    private readonly NeoRpcOptions neoSettings = neoRpcOptions.Value;

    public bool IsMainNet =>
        string.Equals(neoSettings.Network, "neo3:mainnet", StringComparison.Ordinal);

    public async Task<PaymentIntentResponse> CreateIntentAsync(
        string projectId,
        string payerAddress,
        string payerScriptHash,
        CancellationToken ct
    )
    {
        var recipientScriptHash = GetRecipientScriptHash();
        var now = DateTime.UtcNow;
        var intent = new RelayPaymentIntentDocument
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = projectId.Trim(),
            PayerAddress = payerAddress,
            PayerScriptHash = NormalizeHash(payerScriptHash),
            RecipientAddress = settings.PaymentRecipientAddress.Trim(),
            RecipientScriptHash = recipientScriptHash,
            RequiredGasDatoshis = settings.PaidPlanGasDatoshis,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(settings.PaymentIntentMinutes)
        };
        await db.PaymentIntents.InsertOneAsync(intent, cancellationToken: ct);

        return ToResponse(intent);
    }

    public async Task<PaymentResponse> ConfirmAsync(
        string projectId,
        string intentId,
        string transactionId,
        CancellationToken ct
    )
    {
        EnsureAvailable();
        var intent = await db
            .PaymentIntents.Find(x => x.Id == intentId && x.ProjectId == projectId)
            .FirstOrDefaultAsync(ct);

        if (intent is null)
            throw new PaymentValidationException("Payment intent was not found.", 404);

        if (intent.Status == "confirmed")
            return new PaymentResponse(
                intent.ConfirmedTransactionId ?? string.Empty,
                intent.Id,
                "confirmed",
                await PaidUntilAsync(projectId, ct)
            );

        if (intent.Status is not ("pending" or "awaiting_finality"))
            throw new PaymentValidationException("This payment intent is no longer usable.", 409);

        if (intent.ExpiresAt <= DateTime.UtcNow)
        {
            await db.PaymentIntents.UpdateOneAsync(
                x => x.Id == intent.Id,
                Builders<RelayPaymentIntentDocument>.Update.Set(x => x.Status, "expired"),
                cancellationToken: ct
            );

            throw new PaymentValidationException(
                "Payment intent expired. Create a new one before sending GAS.",
                409
            );
        }

        var normalizedTransactionId = NormalizeTransactionId(transactionId);

        if (
            !string.IsNullOrWhiteSpace(intent.SubmittedTransactionId)
            && !string.Equals(
                intent.SubmittedTransactionId,
                normalizedTransactionId,
                StringComparison.Ordinal
            )
        )
            throw new PaymentValidationException(
                "This payment intent is already associated with another transaction.",
                409
            );

        try
        {
            var association = await db.PaymentIntents.UpdateOneAsync(
                Builders<RelayPaymentIntentDocument>.Filter.And(
                    Builders<RelayPaymentIntentDocument>.Filter.Eq(item => item.Id, intent.Id),
                    Builders<RelayPaymentIntentDocument>.Filter.Or(
                        Builders<RelayPaymentIntentDocument>.Filter.Eq(
                            item => item.SubmittedTransactionId,
                            null
                        ),
                        Builders<RelayPaymentIntentDocument>.Filter.Eq(
                            item => item.SubmittedTransactionId,
                            normalizedTransactionId
                        )
                    )
                ),
                Builders<RelayPaymentIntentDocument>
                    .Update.Set(item => item.SubmittedTransactionId, normalizedTransactionId)
                    .Set(item => item.SubmittedAt, DateTime.UtcNow),
                cancellationToken: ct
            );

            if (association.MatchedCount != 1)
                throw new PaymentValidationException(
                    "This payment intent is already associated with another transaction.",
                    409
                );
        }
        catch (MongoWriteException)
        {
            throw new PaymentValidationException(
                "This transaction is already associated with another payment intent.",
                409
            );
        }

        var claimed = await db
            .Payments.Find(x => x.Id == normalizedTransactionId)
            .FirstOrDefaultAsync(ct);

        if (claimed is not null)
        {
            if (!string.Equals(claimed.IntentId, intent.Id, StringComparison.Ordinal))
                throw new PaymentValidationException(
                    "This transaction has already been used for another payment.",
                    409
                );

            var existingEntitlements = await entitlements.GrantPaidAccessAsync(
                projectId,
                claimed.Id,
                ct
            );
            var existingPaidUntil = existingEntitlements
                .First(x => x.Network == "neo3:mainnet")
                .PeriodEndsAt;
            await db.PaymentIntents.UpdateOneAsync(
                x => x.Id == intent.Id,
                Builders<RelayPaymentIntentDocument>
                    .Update.Set(x => x.Status, "confirmed")
                    .Set(x => x.ConfirmedTransactionId, claimed.Id)
                    .Set(x => x.ConfirmedAt, DateTime.UtcNow),
                cancellationToken: ct
            );

            return new PaymentResponse(claimed.Id, intent.Id, "confirmed", existingPaidUntil);
        }

        var verification = await verifier.VerifyAsync(normalizedTransactionId, intent, ct);

        if (verification.Status == PaymentVerificationStatus.AwaitingFinality)
        {
            await db.PaymentIntents.UpdateOneAsync(
                item => item.Id == intent.Id,
                Builders<RelayPaymentIntentDocument>.Update.Set(
                    item => item.Status,
                    "awaiting_finality"
                ),
                cancellationToken: ct
            );

            return new PaymentResponse(
                normalizedTransactionId,
                intent.Id,
                "awaiting_finality",
                null,
                $"Waiting for {Math.Max(1, settings.PaymentConfirmationBlocks)} MainNet confirmations."
            );
        }

        if (verification.Status == PaymentVerificationStatus.Rejected)
        {
            await db.PaymentIntents.UpdateOneAsync(
                item => item.Id == intent.Id,
                Builders<RelayPaymentIntentDocument>
                    .Update.Set(item => item.Status, "rejected")
                    .Set(
                        item => item.RejectionReason,
                        "The transaction did not contain a successful required GAS transfer."
                    ),
                cancellationToken: ct
            );

            throw new PaymentValidationException(
                "The confirmed transaction does not contain the required GAS transfer from this wallet.",
                409
            );
        }

        var payment = new RelayPaymentDocument
        {
            Id = normalizedTransactionId,
            IntentId = intent.Id,
            ProjectId = intent.ProjectId,
            SenderScriptHash = intent.PayerScriptHash,
            RecipientScriptHash = intent.RecipientScriptHash,
            GasDatoshis = verification.GasDatoshis,
            Network = "neo3:mainnet",
            BlockIndex = verification.BlockIndex,
            VerifiedAt = DateTime.UtcNow
        };

        try
        {
            await db.Payments.InsertOneAsync(payment, cancellationToken: ct);
        }
        catch (MongoWriteException)
        {
            return await ConfirmAsync(projectId, intentId, normalizedTransactionId, ct);
        }

        var granted = await entitlements.GrantPaidAccessAsync(projectId, payment.Id, ct);
        var paidUntil = granted.First(x => x.Network == "neo3:mainnet").PeriodEndsAt;
        await db.Payments.UpdateOneAsync(
            x => x.Id == payment.Id,
            Builders<RelayPaymentDocument>.Update.Set(x => x.EntitlementEndsAt, paidUntil),
            cancellationToken: ct
        );
        await db.PaymentIntents.UpdateOneAsync(
            x => x.Id == intent.Id,
            Builders<RelayPaymentIntentDocument>
                .Update.Set(x => x.Status, "confirmed")
                .Set(x => x.ConfirmedTransactionId, payment.Id)
                .Set(x => x.ConfirmedAt, DateTime.UtcNow),
            cancellationToken: ct
        );

        return new PaymentResponse(payment.Id, intent.Id, "confirmed", paidUntil);
    }

    public async Task<PaymentHistoryResponse> HistoryAsync(string projectId, CancellationToken ct)
    {
        await db.PaymentIntents.UpdateManyAsync(
            item =>
                item.ProjectId == projectId
                && (item.Status == "pending" || item.Status == "awaiting_finality")
                && item.ExpiresAt <= DateTime.UtcNow,
            Builders<RelayPaymentIntentDocument>.Update.Set(item => item.Status, "expired"),
            cancellationToken: ct
        );
        var payments = await db
            .Payments.Find(x => x.ProjectId == projectId)
            .SortByDescending(x => x.VerifiedAt)
            .Limit(25)
            .ToListAsync(ct);
        var history = await db
            .EntitlementHistory.Find(x => x.ProjectId == projectId)
            .SortByDescending(x => x.CreatedAt)
            .Limit(50)
            .ToListAsync(ct);
        var pending = await db
            .PaymentIntents.Find(x =>
                x.ProjectId == projectId
                && (x.Status == "pending" || x.Status == "awaiting_finality")
            )
            .SortByDescending(x => x.CreatedAt)
            .Limit(10)
            .ToListAsync(ct);

        return new PaymentHistoryResponse(
            payments
                .Select(x => new PaymentResponse(
                    x.Id,
                    x.IntentId,
                    "confirmed",
                    x.EntitlementEndsAt
                ))
                .ToArray(),
            history
                .Select(x => new EntitlementHistoryResponse(
                    x.Network,
                    x.Plan,
                    x.PeriodStart,
                    x.PeriodEndsAt,
                    x.GraceEndsAt
                ))
                .ToArray(),
            pending.Select(ToResponse).ToArray()
        );
    }

    private async Task<DateTime?> PaidUntilAsync(string projectId, CancellationToken ct) =>
        (await entitlements.GetAsync(projectId, "neo3:mainnet", ct)).PaidUntil;

    private void EnsureAvailable()
    {
        if (!IsMainNet)
            throw new PaymentValidationException(
                "Payments are available from the MainNet relay only.",
                404
            );

        if (
            !TryGetScriptHashFromAddress(settings.PaymentRecipientAddress, out _)
            || settings.PaidPlanGasDatoshis <= 0
        )
            throw new PaymentValidationException(
                "Paid Relay is not configured yet. Set the MainNet payment recipient before launching payments.",
                503
            );
    }

    private string GetRecipientScriptHash()
    {
        EnsureAvailable();

        return TryGetScriptHashFromAddress(settings.PaymentRecipientAddress, out var scriptHash)
            ? scriptHash
            : throw new PaymentValidationException(
                "Paid Relay is not configured yet. Set the MainNet payment recipient before launching payments.",
                503
            );
    }

    private static PaymentIntentResponse ToResponse(RelayPaymentIntentDocument intent) =>
        new(
            intent.Id,
            intent.ProjectId,
            intent.Network,
            intent.RecipientAddress,
            intent.RecipientScriptHash,
            intent.RequiredGasDatoshis,
            intent.Status,
            intent.CreatedAt,
            intent.ExpiresAt,
            intent.ConfirmedTransactionId,
            intent.SubmittedTransactionId
        );
}

public sealed class PaymentValidationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
