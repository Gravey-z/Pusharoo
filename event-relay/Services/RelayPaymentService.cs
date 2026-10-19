using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using static Pusharoo.EventRelay.Services.NeoPaymentEncoding;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayPaymentService(
    MongoDbContext db,
    RelayPaymentVerifier verifier,
    RelayEntitlementService entitlements,
    IOptions<EventRelayOptions> relayOptions,
    IOptions<NeoRpcOptions> neoRpcOptions,
    TimeProvider? timeProvider = null,
    RelayPackageStore? packageStore = null,
    RelayPackageCatalog? packageCatalog = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

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
        if (packageStore is not null && await packageStore.DescribeAsync(ct) is { BillingReady: false } readiness)
            throw new PaymentValidationException(readiness.BillingBlocker!, 503);
        if (await db.Entitlements.Find(x => x.ProjectId == projectId && x.PackageId == "pro").AnyAsync(ct)
            || await db.PurchasedPeriods.Find(x => x.ProjectId == projectId && x.EffectiveTerms.PackageId == "pro").AnyAsync(ct))
            throw new PaymentValidationException("Select a package explicitly; package-aware purchases are not enabled yet.", 409);
        var now = clock.GetUtcNow().UtcDateTime;
        var quotedPackage = RelayPackageCatalog.LegacyTerms(settings);
        if (quotedPackage.PackageId == "starter")
            quotedPackage = (packageCatalog ?? new RelayPackageCatalog(Microsoft.Extensions.Options.Options.Create(new RelayPackageOptions()))).Get("starter");
        var intent = new RelayPaymentIntentDocument
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = projectId.Trim(),
            PayerAddress = payerAddress,
            PayerScriptHash = NormalizeHash(payerScriptHash),
            RecipientAddress = settings.PaymentRecipientAddress.Trim(),
            RecipientScriptHash = recipientScriptHash,
            RequiredGasDatoshis = settings.PaidPlanGasDatoshis,
            Terms = new RelayPurchaseTerms { Package = quotedPackage,
                RequiredGasDatoshis = settings.PaidPlanGasDatoshis, QuotedAt = now, Source = "legacy_purchase_path" },
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
        {
            var confirmedTerms = intent.Terms ?? (await db.Payments.Find(x => x.Id == intent.ConfirmedTransactionId).FirstOrDefaultAsync(ct))?.Terms;
            return new PaymentResponse(
                intent.ConfirmedTransactionId ?? string.Empty,
                intent.Id,
                "confirmed",
                await PaidUntilAsync(projectId, ct),
                Terms: confirmedTerms
            );
        }

        if (intent.Status is not ("pending" or "awaiting_finality"))
            throw new PaymentValidationException("This payment intent is no longer usable.", 409);

        if (intent.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
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
                    .Set(item => item.SubmittedAt, clock.GetUtcNow().UtcDateTime),
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

        var purchasedTerms = claimed?.Terms ?? intent.Terms ?? await LegacyTermsAsync(intent, ct);

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
                ct,
                purchasedTerms
            );
            var existingPaidUntil = existingEntitlements
                .First(x => x.Network == "neo3:mainnet")
                .PeriodEndsAt;
            await RecordPurchasedPeriodAsync(projectId, claimed.Id, purchasedTerms, existingEntitlements, ct);
            await db.PaymentIntents.UpdateOneAsync(
                x => x.Id == intent.Id,
                Builders<RelayPaymentIntentDocument>
                    .Update.Set(x => x.Status, "confirmed")
                    .Set(x => x.ConfirmedTransactionId, claimed.Id)
                    .Set(x => x.ConfirmedAt, clock.GetUtcNow().UtcDateTime),
                cancellationToken: ct
            );

            return new PaymentResponse(claimed.Id, intent.Id, "confirmed", existingPaidUntil, Terms: purchasedTerms);
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
            VerifiedAt = clock.GetUtcNow().UtcDateTime,
            Terms = purchasedTerms,
        };

        try
        {
            await db.Payments.InsertOneAsync(payment, cancellationToken: ct);
        }
        catch (MongoWriteException)
        {
            return await ConfirmAsync(projectId, intentId, normalizedTransactionId, ct);
        }

        var granted = await entitlements.GrantPaidAccessAsync(projectId, payment.Id, ct, purchasedTerms);
        await RecordPurchasedPeriodAsync(projectId, payment.Id, purchasedTerms, granted, ct);
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
                .Set(x => x.ConfirmedAt, clock.GetUtcNow().UtcDateTime),
            cancellationToken: ct
        );

        return new PaymentResponse(payment.Id, intent.Id, "confirmed", paidUntil, Terms: purchasedTerms);
    }

    public async Task<PaymentHistoryResponse> HistoryAsync(string projectId, CancellationToken ct)
    {
        await db.PaymentIntents.UpdateManyAsync(
            item =>
                item.ProjectId == projectId
                && (item.Status == "pending" || item.Status == "awaiting_finality")
                && item.ExpiresAt <= clock.GetUtcNow().UtcDateTime,
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
                    x.EntitlementEndsAt,
                    Terms: x.Terms
                ))
                .ToArray(),
            history
                .Select(x => new EntitlementHistoryResponse(
                    x.Network,
                    x.Plan,
                    x.PeriodStart,
                    x.PeriodEndsAt,
                    x.GraceEndsAt,
                    x.Terms,
                    x.PeriodId
                ))
                .ToArray(),
            pending.Select(ToResponse).ToArray()
        );
    }

    private async Task<DateTime?> PaidUntilAsync(string projectId, CancellationToken ct) =>
        (await entitlements.GetAsync(projectId, "neo3:mainnet", ct)).PaidUntil;

    private async Task<RelayPurchaseTerms> LegacyTermsAsync(RelayPaymentIntentDocument intent, CancellationToken ct)
    {
        var baselines = await db.BillingSchema.Find(new MongoDB.Bson.BsonDocument("_id", new MongoDB.Bson.BsonDocument("$regex", "^legacy:"))).Limit(2).ToListAsync(ct);
        if (baselines.Count != 1)
            throw new PaymentValidationException("Legacy payment terms need a recorded, unambiguous deployment baseline before confirmation; no payment was discarded.", 503);
        var terms = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<RelayPackageTerms>(baselines[0]["terms"].AsBsonDocument);
        if (terms.GasDatoshis != intent.RequiredGasDatoshis)
            throw new PaymentValidationException("Legacy quote price differs from the recorded baseline; explicit reconciliation is required.", 503);
        return new() { Package = terms, RequiredGasDatoshis = intent.RequiredGasDatoshis,
            QuotedAt = intent.CreatedAt, Source = "legacy_baseline:" + baselines[0]["_id"].AsString };
    }

    private async Task RecordPurchasedPeriodAsync(string projectId, string paymentId, RelayPurchaseTerms terms,
        IReadOnlyList<RelayEntitlementDocument> granted, CancellationToken ct)
    {
        var mainnet = granted.First(x => x.Network == "neo3:mainnet");
        var testnet = granted.First(x => x.Network == "neo3:testnet");
        var mainGrant = mainnet.PaymentGrants.Single(x => x.PaymentId == paymentId);
        var testGrant = testnet.PaymentGrants.Single(x => x.PaymentId == paymentId);
        var store = packageStore ?? new RelayPackageStore(db, new RelayPackageCatalog(Microsoft.Extensions.Options.Options.Create(new RelayPackageOptions())), relayOptions);
        await store.InsertPeriodAsync(new() { Id = $"{projectId}:{paymentId}", ProjectId = projectId, PurchaseId = paymentId,
            StartsAt = mainGrant.PeriodStart, EndsAt = mainGrant.PeriodEndsAt, OriginalTerms = mainGrant.Terms?.Package ?? terms.Package,
            EffectiveTerms = mainGrant.Terms?.Package ?? terms.Package, Source = "legacy_purchase_path",
            MainnetLedgerId = RelayEntitlementService.PeriodId(mainnet with { PeriodStart = mainGrant.PeriodStart, LastPaymentId = paymentId }),
            TestnetLedgerId = RelayEntitlementService.PeriodId(testnet with { PeriodStart = testGrant.PeriodStart, LastPaymentId = paymentId }) }, ct);
    }

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
            intent.SubmittedTransactionId,
            intent.Terms
        );
}

public sealed class PaymentValidationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
