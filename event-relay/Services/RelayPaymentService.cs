using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
using static Pusharoo.EventRelay.Services.NeoPaymentEncoding;

namespace Pusharoo.EventRelay.Services;

public sealed partial class RelayPaymentService(
    MongoDbContext db,
    RelayPaymentVerifier verifier,
    RelayEntitlementService entitlements,
    IOptions<EventRelayOptions> relayOptions,
    IOptions<NeoRpcOptions> neoRpcOptions,
    TimeProvider? timeProvider = null,
    RelayPackageStore? packageStore = null,
    RelayPackageCatalog? packageCatalog = null,
    IRelayPaymentProjector? paymentProjector = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly EventRelayOptions settings = relayOptions.Value;

    private RelayPackageCatalog Catalog =>
        packageCatalog
        ?? new(Microsoft.Extensions.Options.Options.Create(new RelayPackageOptions()));

    private RelayPackageStore Store => packageStore ?? new(db, Catalog, relayOptions);

    private RelayBillingAdmissionService Admission => new(db, clock);

    private RelayPaymentApplicationService Applications =>
        new(
            db,
            Store,
            paymentProjector
                ?? new RelayPaymentProjector(
                    db,
                    entitlements,
                    new WebhookSubscriptionRepository(db),
                    clock
                ),
            entitlements.Lifecycle
        );

    public bool IsMainNet => neoRpcOptions.Value.Network == "neo3:mainnet";

    private DateTime Now() =>
        DateTimeOffset
            .FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds())
            .UtcDateTime;

    public Task<PaymentIntentResponse> CreateIntentAsync(
        string projectId,
        string payerAddress,
        string payerScriptHash,
        CancellationToken ct
    ) => CreateIntentAsync(projectId, payerAddress, payerScriptHash, new(), ct);

    public async Task<PaymentIntentResponse> CreateIntentAsync(
        string projectId,
        string payerAddress,
        string payerScriptHash,
        CreateRelayPaymentIntentRequest request,
        CancellationToken ct
    )
    {
        EnsureAvailable();

        if (await Store.DescribeAsync(ct) is { BillingReady: false } readiness)
            throw new PaymentValidationException(readiness.BillingBlocker!, 503);

        ValidateRequest(request);
        var normalizedPayer = NormalizeHash(payerScriptHash);
        var key = request.RequestKey ?? Guid.NewGuid().ToString("N");
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        new
                        {
                            request.PackageId,
                            request.Operation,
                            PeriodIds = request.PeriodIds?.Order(StringComparer.Ordinal).ToArray(),
                            Payer = normalizedPayer,
                        }
                    )
                )
            )
        );
        var intent = await Admission.ReserveAsync(
            projectId,
            key,
            fingerprint,
            async () =>
                await QuoteAsync(
                    projectId,
                    payerAddress,
                    normalizedPayer,
                    request,
                    key,
                    fingerprint,
                    ct
                ),
            ct
        );

        return ToResponse(intent);
    }

    private async Task<RelayPaymentIntentDocument> QuoteAsync(
        string projectId,
        string payerAddress,
        string payerHash,
        CreateRelayPaymentIntentRequest request,
        string key,
        string fingerprint,
        CancellationToken ct
    )
    {
        var now = Now();
        var periods = await db.PurchasedPeriods.Find(x => x.ProjectId == projectId).ToListAsync(ct);
        var existing = await db
            .Entitlements.Find(x => x.ProjectId == projectId && x.Plan == "paid")
            .ToListAsync(ct);
        var packageId = request.PackageId;

        if (packageId is null)
        {
            if (
                periods.Any(x => x.EffectiveTerms.PackageId != "starter")
                || existing.Any(x => RelayPackageCompatibility.PackageId(x) != "starter")
            )
                throw new PaymentValidationException(
                    "Select a package explicitly for a project with Pro or custom paid access.",
                    409
                );

            packageId = "starter";
        }

        var package = Catalog.Get(packageId);
        var tail = periods
            .Select(x => (DateTime?)x.EndsAt)
            .Concat(existing.Select(x => x.PaidUntil))
            .Max();
        var targets = new List<RelayPeriodTarget>();
        RelayBonusTerms? bonus = null;
        long amount = package.GasDatoshis;

        if (request.Operation == "upgrade_periods")
        {
            foreach (var id in request.PeriodIds!.Order(StringComparer.Ordinal))
            {
                var period = periods.SingleOrDefault(x => x.Id == id);

                if (
                    period is null
                    || period.EffectiveTerms.PackageId != "starter"
                    || period.EndsAt <= now
                    || period.Source is "legacy_carryover" or "legacy_custom"
                )
                    throw new PaymentValidationException(
                        "Every upgrade target must be an eligible unexpired Starter period owned by this project.",
                        409
                    );

                targets.Add(
                    Target(
                        period,
                        package,
                        checked(package.GasDatoshis - Catalog.Get("starter").GasDatoshis)
                    )
                );
            }

            amount = checked(targets.Sum(x => x.PriceDatoshis));
        }
        else if (package.PackageId == "pro")
        {
            var current = periods
                .Where(x =>
                    x.StartsAt <= now
                    && now < x.EndsAt
                    && x.EffectiveTerms.PackageId == "starter"
                    && x.Source is not ("legacy_carryover" or "legacy_custom")
                )
                .ToArray();

            if (current.Length > 1)
                throw new PaymentValidationException(
                    "Overlapping paid periods require reconciliation before quoting the Pro bonus.",
                    409
                );

            if (current.SingleOrDefault() is { } active)
            {
                bool Maps(string network, string? ledgerId) =>
                    existing.Any(x =>
                        x.Network == network
                        && (
                            x.PurchasedPeriodId == active.Id
                            || RelayEntitlementService.PeriodId(x) == ledgerId
                        )
                    );

                if (
                    !Maps("neo3:mainnet", active.MainnetLedgerId)
                    || !Maps("neo3:testnet", active.TestnetLedgerId)
                )
                    throw new PaymentValidationException(
                        "Reconcile the active Starter period's network ledgers before quoting its free Pro upgrade.",
                        409
                    );

                bonus = new() { Target = Target(active, package, 0) };
            }
        }

        var terms = new RelayPurchaseTerms
        {
            Operation = request.Operation,
            Package = package,
            RequiredGasDatoshis = amount,
            QuotedAt = now,
            Targets = targets.ToArray(),
            Bonus = bonus,
            ScheduleTail = tail,
            EffectiveRule =
                request.Operation == "purchase_period"
                    ? "append_after_paid_schedule"
                    : "selected_periods_keep_dates_and_usage",
        };
        var expires = now.AddMinutes(settings.PaymentIntentMinutes);

        foreach (var target in targets.Where(x => x.StartsAt <= now))
            if (target.EndsAt < expires)
                expires = target.EndsAt;

        TryGetScriptHashFromAddress(settings.PaymentRecipientAddress, out var recipient);

        return new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = projectId,
            RequestKey = key,
            RequestFingerprint = fingerprint,
            PayerAddress = payerAddress,
            PayerScriptHash = payerHash,
            RecipientAddress = settings.PaymentRecipientAddress,
            RecipientScriptHash = recipient,
            RequiredGasDatoshis = amount,
            Terms = terms,
            CreatedAt = now,
            ExpiresAt = expires,
            ConfirmationBlocks = Math.Max(2, settings.PaymentConfirmationBlocks),
        };
    }

    private static RelayPeriodTarget Target(
        RelayPurchasedPeriodDocument period,
        RelayPackageTerms target,
        long amount
    ) =>
        new()
        {
            PeriodId = period.Id,
            ExpectedVersion = period.Version,
            OriginalTerms = period.EffectiveTerms,
            TargetTerms = target,
            StartsAt = period.StartsAt,
            EndsAt = period.EndsAt,
            PriceDatoshis = amount,
        };

    private static void ValidateRequest(CreateRelayPaymentIntentRequest request)
    {
        if (
            request.Operation is not ("purchase_period" or "upgrade_periods")
            || request.PackageId is not (null or "starter" or "pro")
            || request.RequestKey is { } key
                && (
                    key.Length is < 1 or > 128
                    || key.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_' or ':'))
                )
        )
            throw new PaymentValidationException("Invalid package, operation or request key.", 400);

        if (
            request.Operation == "upgrade_periods"
                && (
                    request.PackageId != "pro"
                    || request.PeriodIds is not { Length: > 0 and <= 100 }
                    || request.PeriodIds.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256)
                    || request.PeriodIds.Distinct(StringComparer.Ordinal).Count()
                        != request.PeriodIds.Length
                )
            || request.Operation == "purchase_period" && request.PeriodIds is { Length: > 0 }
        )
            throw new PaymentValidationException(
                "Upgrades require Pro and 1-100 distinct period IDs; purchases cannot specify upgrade targets.",
                400
            );
    }

    public async Task<PaymentResponse> ConfirmAsync(
        string projectId,
        string intentId,
        string transactionId,
        CancellationToken ct
    )
    {
        if (!IsMainNet)
            throw new PaymentValidationException(
                "Payments are available from the Mainnet relay only.",
                404
            );

        var intent =
            await db
                .PaymentIntents.Find(x => x.Id == intentId && x.ProjectId == projectId)
                .FirstOrDefaultAsync(ct)
            ?? throw new PaymentValidationException("Payment intent was not found.", 404);
        var tx = NormalizeTransactionId(transactionId);

        if (
            intent.SubmittedTransactionId is { } bound && bound != tx
            || intent.ConfirmedTransactionId is { } confirmed && confirmed != tx
        )
            throw new PaymentValidationException(
                "This intent is already bound to another transaction.",
                409
            );

        if (intent.Status is "confirmed" or "resolved")
            return await ResponseAsync(intent, tx, ct);

        if (
            intent.Status == "reconciliation_required"
            && intent.ActiveResolutionId is null
            && intent.CompletedResolutions.Length > 0
        )
            return await ResponseAsync(intent, tx, ct);

        if (intent.Status is "rejected" or "cancelled")
            throw new PaymentValidationException("This intent was rejected or cancelled.", 409);

        var claim = await ClaimTransactionAsync(tx, intent.Id, "payment", ct, intent.SubmittedAt);
        var knownPayment = await db.Payments.Find(x => x.Id == tx).FirstOrDefaultAsync(ct);

        if (knownPayment is not null && knownPayment.IntentId != intent.Id)
            throw new PaymentValidationException(
                "Transaction was already used for another intent.",
                409
            );

        if (intent.SubmittedTransactionId is null)
        {
            var now = ClaimTime(claim);

            try
            {
                await db.PaymentIntents.UpdateOneAsync(
                    x => x.Id == intent.Id && x.SubmittedTransactionId == null,
                    Builders<RelayPaymentIntentDocument>
                        .Update.Set(x => x.SubmittedTransactionId, tx)
                        .Set(x => x.SubmittedAt, now)
                        .Set(x => x.Status, "pending")
                        .Set(
                            x => x.LateSubmission,
                            now >= intent.ExpiresAt
                                && (
                                    knownPayment == null
                                    || knownPayment.VerifiedAt >= intent.ExpiresAt
                                )
                        ),
                    cancellationToken: ct
                );
            }
            catch (MongoWriteException error)
                when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new PaymentValidationException(
                    "Transaction is already associated with another intent.",
                    409
                );
            }

            intent = await db.PaymentIntents.Find(x => x.Id == intent.Id).SingleAsync(ct);

            if (intent.SubmittedTransactionId != tx)
                throw new PaymentValidationException(
                    "This intent is bound to another transaction.",
                    409
                );
        }

        await db.PaymentTransactions.UpdateOneAsync(
            new BsonDocument("_id", tx),
            new BsonDocument("$set", new BsonDocument("bindingComplete", true)),
            cancellationToken: ct
        );

        if (intent.ActiveResolutionId is not null)
            return await RecoverResolutionAsync(intent, ct);

        var payment = await db.Payments.Find(x => x.Id == tx).FirstOrDefaultAsync(ct);

        if (payment is not null && payment.IntentId != intent.Id)
            throw new PaymentValidationException(
                "Transaction was already used for another intent.",
                409
            );

        if (payment is null)
        {
            var terms = intent.Terms ?? await LegacyTermsAsync(intent, ct);
            var verification = await verifier.VerifyAsync(tx, intent, ct);

            if (verification.Status == PaymentVerificationStatus.AwaitingFinality)
            {
                await SetStateAsync(
                    intent.Id,
                    "awaiting_finality",
                    "Waiting for Mainnet finality; do not send another transfer.",
                    null,
                    ct
                );

                return await ResponseAsync(await IntentAsync(intent.Id, ct), tx, ct);
            }

            if (verification.Status == PaymentVerificationStatus.Rejected)
            {
                await SetStateAsync(
                    intent.Id,
                    "rejected",
                    "No successful required GAS transfer from the quoted payer to recipient was found.",
                    null,
                    ct
                );
                await Admission.ReleaseAsync(projectId, intent.Id, ct);

                throw new PaymentValidationException(
                    "The transaction did not contain the required successful GAS transfer.",
                    409
                );
            }

            var candidate = new RelayPaymentDocument
            {
                Id = tx,
                IntentId = intent.Id,
                ProjectId = projectId,
                SenderScriptHash = intent.PayerScriptHash,
                RecipientScriptHash = intent.RecipientScriptHash,
                GasDatoshis = verification.GasDatoshis,
                Network = "neo3:mainnet",
                BlockIndex = verification.BlockIndex,
                VerifiedAt = Now(),
                Terms = terms,
            };

            try
            {
                await db.Payments.InsertOneAsync(candidate, cancellationToken: ct);
            }
            catch (MongoWriteException error)
                when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }

            payment = await db.Payments.Find(x => x.Id == tx).SingleAsync(ct);
        }

        if (payment.Terms is null)
        {
            var terms = intent.Terms ?? await LegacyTermsAsync(intent, ct);

            await db.Payments.UpdateOneAsync(
                x => x.Id == tx && x.Terms == null,
                Builders<RelayPaymentDocument>.Update.Set(x => x.Terms, terms),
                cancellationToken: ct
            );
            payment = await db.Payments.Find(x => x.Id == tx).SingleAsync(ct);
        }

        if (intent.ApplicationDecisionId is { } applicationId && applicationId != tx)
            return await ResponseAsync(intent, tx, ct);

        await SetStateAsync(
            intent.Id,
            "applying",
            "Payment verified; applying its stored benefits. Do not send another transfer.",
            null,
            ct,
            tx
        );
        var outcome = await Applications.ApplyAsync(intent, payment, ct);
        var state = outcome.Complete
            ? outcome.UnappliedGasDatoshis > 0
                ? "reconciliation_required"
                : "confirmed"
            : outcome.Conflict
                ? "reconciliation_required"
                : "applying";

        await SetStateAsync(
            intent.Id,
            state,
            outcome.Message,
            outcome.UnappliedGasDatoshis,
            ct,
            tx
        );
        var end = outcome
            .Decision.Allocations.Where(x => x.Status == "accepted")
            .Select(x => (DateTime?)x.EndsAt)
            .Max();

        if (end is not null)
            await db.Payments.UpdateOneAsync(
                x => x.Id == tx && (x.EntitlementEndsAt == null || x.EntitlementEndsAt < end),
                Builders<RelayPaymentDocument>.Update.Set(x => x.EntitlementEndsAt, end),
                cancellationToken: ct
            );

        if (state == "confirmed")
            await Admission.ReleaseAsync(projectId, intent.Id, ct);

        return await ResponseAsync(await IntentAsync(intent.Id, ct), tx, ct);
    }

    private async Task<BsonDocument> ClaimTransactionAsync(
        string tx,
        string intentId,
        string kind,
        CancellationToken ct,
        DateTime? originalSubmission = null
    )
    {
        if (
            await db
                .PaymentIntents.Find(x => x.SubmittedTransactionId == tx && x.Id != intentId)
                .AnyAsync(ct)
        )
            throw new PaymentValidationException(
                "Transaction is already associated with another intent.",
                409
            );

        var claim = new BsonDocument
        {
            { "_id", tx },
            { "intentId", intentId },
            { "kind", kind },
            { "claimedAt", originalSubmission ?? Now() },
            { "bindingComplete", kind != "payment" },
        };

        try
        {
            await db.PaymentTransactions.InsertOneAsync(claim, cancellationToken: ct);
        }
        catch (MongoWriteException error)
            when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var stored = await db
                .PaymentTransactions.Find(new BsonDocument("_id", tx))
                .FirstOrDefaultAsync(ct);

            if (stored is null || stored["intentId"] != intentId || stored["kind"] != kind)
                throw new PaymentValidationException(
                    "Transaction was already used for a payment or refund.",
                    409
                );
        }

        return await db.PaymentTransactions.Find(new BsonDocument("_id", tx)).SingleAsync(ct);
    }

    private DateTime ClaimTime(BsonDocument claim) =>
        claim.TryGetValue("claimedAt", out var value) && value is BsonDateTime time
            ? time.ToUniversalTime()
            : Now();

    private async Task<RelayPurchaseTerms> LegacyTermsAsync(
        RelayPaymentIntentDocument intent,
        CancellationToken ct
    )
    {
        var rows = await db
            .BillingSchema.Find(new BsonDocument("_id", new BsonDocument("$regex", "^legacy:")))
            .Limit(2)
            .ToListAsync(ct);

        if (rows.Count != 1)
            throw new PaymentValidationException(
                "Legacy payment terms require an unambiguous recorded deployment baseline; no payment was discarded.",
                503
            );

        var terms = BsonSerializer.Deserialize<RelayPackageTerms>(rows[0]["terms"].AsBsonDocument);

        if (terms.GasDatoshis != intent.RequiredGasDatoshis)
            throw new PaymentValidationException(
                "Legacy quote price differs from its baseline; reconcile explicitly.",
                503
            );

        var tail = (
            await db
                .Entitlements.Find(x => x.ProjectId == intent.ProjectId && x.Plan == "paid")
                .ToListAsync(ct)
        )
            .Select(x => x.PaidUntil)
            .Max();

        return new()
        {
            Package = terms,
            RequiredGasDatoshis = intent.RequiredGasDatoshis,
            QuotedAt = intent.CreatedAt,
            ScheduleTail = tail,
            Source = "legacy_baseline:" + rows[0]["_id"].AsString,
        };
    }

    private Task<RelayPaymentIntentDocument> IntentAsync(string id, CancellationToken ct) =>
        db.PaymentIntents.Find(x => x.Id == id).SingleAsync(ct);

    private async Task SetStateAsync(
        string id,
        string state,
        string? message,
        long? unapplied,
        CancellationToken ct,
        string? decisionId = null
    )
    {
        var update = Builders<RelayPaymentIntentDocument>
            .Update.Set(x => x.Status, state)
            .Set(x => x.RecoveryMessage, message);

        if (unapplied is { } amount)
            update = update.Set(x => x.UnappliedGasDatoshis, amount);

        if (decisionId is not null)
            update = update
                .Set(x => x.ApplicationDecisionId, decisionId)
                .Set(x => x.ConfirmedTransactionId, decisionId);

        if (state == "confirmed")
            update = update.Set(x => x.ConfirmedAt, Now());

        if (state == "rejected")
            update = update.Set(x => x.RejectionReason, message);

        var filter = Builders<RelayPaymentIntentDocument>.Filter.Where(x =>
            x.Id == id && x.Status != "resolved" && x.Status != "confirmed"
        );

        if (decisionId is not null)
            filter &=
                Builders<RelayPaymentIntentDocument>.Filter.Eq(x => x.ActiveResolutionId, null)
                & Builders<RelayPaymentIntentDocument>.Filter.Or(
                    Builders<RelayPaymentIntentDocument>.Filter.Size(
                        x => x.CompletedResolutions,
                        0
                    ),
                    Builders<RelayPaymentIntentDocument>.Filter.Exists(
                        x => x.CompletedResolutions,
                        false
                    )
                );

        if (state == "awaiting_finality" || state == "rejected")
            filter &= Builders<RelayPaymentIntentDocument>.Filter.In(
                x => x.Status,
                ["pending", "awaiting_finality", "expired"]
            );

        await db.PaymentIntents.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    private async Task<PaymentResponse> ResponseAsync(
        RelayPaymentIntentDocument intent,
        string tx,
        CancellationToken ct
    )
    {
        var payment = await db.Payments.Find(x => x.Id == tx).FirstOrDefaultAsync(ct);
        var decisionId = intent.ApplicationDecisionId ?? tx;

        if (intent.ActiveResolutionId is { } resolutionId)
        {
            var resolution = await db
                .PaymentResolutions.Find(x => x.Id == resolutionId)
                .FirstOrDefaultAsync(ct);

            decisionId = resolution?.ReplacementDecisionId ?? decisionId;
        }

        var decision = await db
            .BillingDecisions.Find(x => x.Id == decisionId)
            .FirstOrDefaultAsync(ct);

        return new(
            tx,
            intent.Id,
            intent.Status,
            payment?.EntitlementEndsAt,
            intent.RecoveryMessage,
            payment?.Terms ?? intent.Terms,
            intent.UnappliedGasDatoshis,
            decision?.Allocations,
            decision is null ? [] : await Applications.ProgressAsync(decision.Id, ct)
        );
    }

    public async Task<PaymentHistoryResponse> HistoryAsync(string projectId, CancellationToken ct)
    {
        var now = Now();

        await db.PaymentIntents.UpdateManyAsync(
            x =>
                x.ProjectId == projectId
                && x.Status == "pending"
                && x.SubmittedTransactionId == null
                && x.ExpiresAt <= now,
            Builders<RelayPaymentIntentDocument>.Update.Set(x => x.Status, "expired"),
            cancellationToken: ct
        );
        var payments = await db
            .Payments.Find(x => x.ProjectId == projectId)
            .SortByDescending(x => x.VerifiedAt)
            .Limit(25)
            .ToListAsync(ct);
        var result = new List<PaymentResponse>();

        foreach (var payment in payments)
        {
            var intent = await db
                .PaymentIntents.Find(x => x.Id == payment.IntentId)
                .FirstOrDefaultAsync(ct);

            result.Add(
                intent is null
                    ? new(
                        payment.Id,
                        payment.IntentId,
                        "reconciliation_required",
                        payment.EntitlementEndsAt,
                        "Original intent is unavailable.",
                        payment.Terms
                    )
                    : await ResponseAsync(intent, payment.Id, ct)
            );
        }

        var history = await db
            .EntitlementHistory.Find(x => x.ProjectId == projectId)
            .SortByDescending(x => x.CreatedAt)
            .Limit(100)
            .ToListAsync(ct);
        var pending = await db
            .PaymentIntents.Find(x =>
                x.ProjectId == projectId
                && (
                    x.Status == "pending"
                    || x.Status == "awaiting_finality"
                    || x.Status == "applying"
                    || x.Status == "reconciliation_required"
                    || x.Status == "expired" && x.SubmittedTransactionId != null
                )
            )
            .SortByDescending(x => x.CreatedAt)
            .Limit(25)
            .ToListAsync(ct);
        var resolutions = await db
            .PaymentResolutions.Find(x => x.ProjectId == projectId)
            .SortByDescending(x => x.CreatedAt)
            .Limit(50)
            .ToListAsync(ct);
        var annotated = new List<RelayPaymentResolutionDocument>();

        foreach (var resolution in resolutions)
        {
            var ownerIntent = await db
                .PaymentIntents.Find(x => x.Id == resolution.IntentId)
                .FirstOrDefaultAsync(ct);
            var proof = resolution.RefundTransactionId is null
                ? null
                : await db
                    .PaymentTransactions.Find(
                        new BsonDocument
                        {
                            { "_id", resolution.RefundTransactionId },
                            { "intentId", resolution.Id },
                            { "verified", true },
                        }
                    )
                    .FirstOrDefaultAsync(ct);

            annotated.Add(
                resolution with
                {
                    Outcome =
                        ownerIntent?.CompletedResolutions.Contains(resolution.Id) == true
                            ? "completed"
                        : ownerIntent?.ActiveResolutionId == resolution.Id ? "applying"
                        : "not_applied",
                    ActualRefundGasDatoshis = proof?.GetValue(
                        "actualRefundGasDatoshis",
                        BsonNull.Value
                    )
                        is BsonInt64 amount
                        ? amount.Value
                        : null,
                    ResolvedGasDatoshis = proof?.GetValue("resolvedGasDatoshis", BsonNull.Value)
                        is BsonInt64 resolved
                        ? resolved.Value
                        : null,
                }
            );
        }

        return new(
            result,
            history
                .Select(x => new EntitlementHistoryResponse(
                    x.Network,
                    x.Plan,
                    x.PeriodStart,
                    x.PeriodEndsAt,
                    x.GraceEndsAt,
                    x.Terms,
                    x.PeriodId,
                    x.AllocationId
                ))
                .ToArray(),
            pending.Select(ToResponse).ToArray(),
            annotated,
            await Store.PeriodsAsync(projectId, ct),
            (
                await db
                    .PaymentIntents.Find(x => x.ProjectId == projectId)
                    .SortByDescending(x => x.CreatedAt)
                    .Limit(25)
                    .ToListAsync(ct)
            )
                .Select(ToResponse)
                .ToArray()
        );
    }

    public async Task<(int Processed, int Deferred)> RecoverPendingAsync(CancellationToken ct)
    {
        if (!IsMainNet)
            return (0, 0);

        var deferred = 0;
        var unbound = await db
            .PaymentTransactions.Find(
                new BsonDocument
                {
                    { "kind", "payment" },
                    { "bindingComplete", new BsonDocument("$ne", true) },
                }
            )
            .Sort(new BsonDocument("claimedAt", 1))
            .Limit(25)
            .ToListAsync(ct);

        foreach (var claim in unbound)
        {
            var intentId = claim["intentId"].AsString;
            var tx = claim["_id"].AsString;
            var intent = await db
                .PaymentIntents.Find(x => x.Id == intentId)
                .FirstOrDefaultAsync(ct);

            if (intent is null)
            {
                deferred++;
                continue;
            }

            var payment = await db
                .Payments.Find(x => x.Id == tx && x.IntentId == intent.Id)
                .FirstOrDefaultAsync(ct);
            var submitted = ClaimTime(claim);

            try
            {
                await db.PaymentIntents.UpdateOneAsync(
                    x => x.Id == intent.Id && x.SubmittedTransactionId == null,
                    Builders<RelayPaymentIntentDocument>
                        .Update.Set(x => x.SubmittedTransactionId, tx)
                        .Set(x => x.SubmittedAt, submitted)
                        .Set(
                            x => x.LateSubmission,
                            submitted >= intent.ExpiresAt
                                && (payment == null || payment.VerifiedAt >= intent.ExpiresAt)
                        )
                        .Set(x => x.Status, "pending"),
                    cancellationToken: ct
                );

                if (
                    !await db
                        .PaymentIntents.Find(x =>
                            x.Id == intent.Id && x.SubmittedTransactionId == tx
                        )
                        .AnyAsync(ct)
                )
                {
                    deferred++;
                    continue;
                }

                await db.PaymentTransactions.UpdateOneAsync(
                    new BsonDocument("_id", tx),
                    new BsonDocument("$set", new BsonDocument("bindingComplete", true)),
                    cancellationToken: ct
                );
            }
            catch (MongoWriteException error)
                when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                deferred++;
            }
        }

        var now = Now();
        var pending = await db
            .PaymentIntents.Find(x =>
                x.SubmittedTransactionId != null
                && (
                    x.Status == "pending"
                    || x.Status == "awaiting_finality"
                    || x.Status == "applying"
                    || x.Status == "expired"
                )
                && (x.NextRecoveryAt == null || x.NextRecoveryAt <= now)
            )
            .SortBy(x => x.NextRecoveryAt)
            .ThenBy(x => x.CreatedAt)
            .Limit(25)
            .ToListAsync(ct);
        var processed = 0;

        foreach (var intent in pending)
        {
            if (
                (
                    await db.PaymentIntents.UpdateOneAsync(
                        x => x.Id == intent.Id && x.NextRecoveryAt == intent.NextRecoveryAt,
                        Builders<RelayPaymentIntentDocument>.Update.Set(
                            x => x.NextRecoveryAt,
                            now.AddSeconds(settings.PaymentRecoveryPollSeconds)
                        ),
                        cancellationToken: ct
                    )
                ).ModifiedCount == 0
            )
                continue;

            processed++;

            try
            {
                await ConfirmAsync(intent.ProjectId, intent.Id, intent.SubmittedTransactionId!, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
                when (error
                        is MongoException
                            or IOException
                            or NeoRpcException
                            or PaymentValidationException
                )
            {
                deferred++;
            }
        }

        return (processed, deferred);
    }

    private void EnsureAvailable()
    {
        if (!IsMainNet)
            throw new PaymentValidationException(
                "Payments are available from the Mainnet relay only.",
                404
            );

        if (!TryGetScriptHashFromAddress(settings.PaymentRecipientAddress, out _))
            throw new PaymentValidationException(
                "Set the Mainnet payment recipient before enabling Relay payments.",
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
            intent.Terms,
            intent.UnappliedGasDatoshis,
            intent.RecoveryMessage ?? intent.RejectionReason
        );
}

public sealed class PaymentValidationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
