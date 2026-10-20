using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using static Pusharoo.EventRelay.Services.NeoPaymentEncoding;

namespace Pusharoo.EventRelay.Services;

public sealed partial class RelayPaymentService
{
    // Operator-only entry point, deliberately not exposed through the owner gateway.
    // Records authorization/evidence; never signs or sends a refund transaction.
    public async Task<PaymentResponse> ResolveAsync(RelayPaymentResolutionRequest request, CancellationToken ct)
    {
        if (!IsMainNet) throw new PaymentValidationException("Payment resolutions are available from the Mainnet relay only.", 404);
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128
            || string.IsNullOrWhiteSpace(request.OwnerApproval) || request.OwnerApproval.Length > 1000
            || string.IsNullOrWhiteSpace(request.OperatorReference) || request.OperatorReference.Length > 1000
            || request.Action is not ("refund" or "replacement"))
            throw new PaymentValidationException("Resolution ID, owner approval, operator reference and refund/replacement action are required.", 400);
        var intent = await db.PaymentIntents.Find(x => x.Id == request.IntentId && x.ProjectId == request.ProjectId).FirstOrDefaultAsync(ct)
            ?? throw new PaymentValidationException("Original intent was not found.", 404);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        var resolution = await db.PaymentResolutions.Find(x => x.Id == request.Id).FirstOrDefaultAsync(ct);
        if (resolution is not null && (resolution.RequestFingerprint != fingerprint || resolution.IntentId != intent.Id || resolution.ProjectId != intent.ProjectId))
            throw new PaymentValidationException("Resolution ID was reused with different evidence or terms.", 409);
        if (intent.CompletedResolutions.Contains(request.Id)) return await ResponseAsync(intent, intent.SubmittedTransactionId!, ct);
        if (intent.ActiveResolutionId is { } active && active != request.Id)
            throw new PaymentValidationException("A different resolution is already applying.", 409);
        if (intent.UnappliedGasDatoshis <= 0 || intent.Status is not ("reconciliation_required" or "applying"))
            throw new PaymentValidationException("This payment has no verified unapplied balance to resolve.", 409);
        var decision = await db.BillingDecisions.Find(x => x.Id == (intent.ApplicationDecisionId ?? intent.SubmittedTransactionId)).FirstOrDefaultAsync(ct);
        if (decision is null) throw new PaymentValidationException("Recover the original payment decision first.", 409);
        var progress = await Applications.ProgressAsync(decision.Id, ct);
        if (intent.ActiveResolutionId is null && decision.Allocations.Where(x => x.Status == "accepted").Any(x =>
            !progress.Any(p => p.AllocationId == x.Id && p.AppliedNetworks.Contains("neo3:mainnet") && p.AppliedNetworks.Contains("neo3:testnet"))))
            throw new PaymentValidationException("Complete the accepted grants before resolving the unapplied balance.", 409);

        if (resolution is null)
        {
            RelayPaymentIntentDocument? replacement = null;
            string? refund = null;
            if (request.Action == "replacement")
            {
                if (request.Replacement is null || request.RefundTransactionId is not null)
                    throw new PaymentValidationException("A replacement requires a fresh package/period selection and no refund transaction.", 400);
                if (await Store.DescribeAsync(ct) is { BillingReady: false }) throw new PaymentValidationException("Resolve catalog readiness before a replacement quote.", 503);
                ValidateRequest(request.Replacement);
                replacement = await QuoteAsync(intent.ProjectId, intent.PayerAddress, intent.PayerScriptHash,
                    request.Replacement, request.Id, fingerprint, ct);
                replacement = replacement with { RecipientAddress = intent.RecipientAddress, RecipientScriptHash = intent.RecipientScriptHash };
                if (replacement.RequiredGasDatoshis != intent.UnappliedGasDatoshis)
                    throw new PaymentValidationException("Replacement quote must equal the complete unapplied balance; no extra transfer or hidden credit is permitted.", 409);
            }
            else
            {
                if (request.RefundTransactionId is null || request.Replacement is not null)
                    throw new PaymentValidationException("Refund resolution requires the actual refund transaction and no replacement selection.", 400);
                refund = NormalizeTransactionId(request.RefundTransactionId);
            }
            var candidate = new RelayPaymentResolutionDocument { Id = request.Id, ProjectId = intent.ProjectId, IntentId = intent.Id,
                Action = request.Action, OwnerApproval = request.OwnerApproval, OperatorReference = request.OperatorReference,
                AmountDatoshis = intent.UnappliedGasDatoshis, RefundTransactionId = refund, ReplacementQuote = replacement,
                ReplacementDecisionId = replacement is null ? null : $"replacement:{intent.SubmittedTransactionId}:{request.Id}",
                RequestFingerprint = fingerprint, CreatedAt = replacement?.CreatedAt ?? Now() };
            try { await db.PaymentResolutions.InsertOneAsync(candidate, cancellationToken: ct); }
            catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
            resolution = await db.PaymentResolutions.Find(x => x.Id == request.Id).SingleAsync(ct);
        }
        if (resolution.RequestFingerprint != fingerprint || resolution.IntentId != intent.Id || resolution.ProjectId != intent.ProjectId)
            throw new PaymentValidationException("Resolution ID was reused with different evidence or terms.", 409);
        var claimed = await db.PaymentIntents.UpdateOneAsync(x => x.Id == intent.Id && x.UnappliedGasDatoshis == resolution.AmountDatoshis
            && (x.ActiveResolutionId == null || x.ActiveResolutionId == resolution.Id) && !x.CompletedResolutions.Contains(resolution.Id),
            Builders<RelayPaymentIntentDocument>.Update.Set(x => x.ActiveResolutionId, resolution.Id).Set(x => x.Status, "applying"), cancellationToken: ct);
        intent = await IntentAsync(intent.Id, ct);
        if (claimed.MatchedCount == 0 && intent.ActiveResolutionId != resolution.Id)
        {
            if (intent.CompletedResolutions.Contains(resolution.Id)) return await ResponseAsync(intent, intent.SubmittedTransactionId!, ct);
            throw new PaymentValidationException("Unapplied balance or active resolution changed; review the current state.", 409);
        }
        return await RecoverResolutionAsync(intent, ct);
    }

    private async Task<PaymentResponse> RecoverResolutionAsync(RelayPaymentIntentDocument intent, CancellationToken ct)
    {
        var resolution = await db.PaymentResolutions.Find(x => x.Id == intent.ActiveResolutionId).SingleAsync(ct);
        var unapplied = resolution.AmountDatoshis;
        string? applicationId = intent.ApplicationDecisionId;
        if (resolution.Action == "refund")
        {
            var proof = await db.PaymentTransactions.Find(new BsonDocument { { "_id", resolution.RefundTransactionId }, { "verified", true }, { "intentId", resolution.Id } }).FirstOrDefaultAsync(ct);
            if (proof is null)
            {
                var refundIntent = new RelayPaymentIntentDocument { PayerScriptHash = intent.RecipientScriptHash,
                    RecipientScriptHash = intent.PayerScriptHash, RequiredGasDatoshis = 1, ConfirmationBlocks = Math.Max(2, settings.PaymentConfirmationBlocks) };
                var result = await verifier.VerifyAsync(resolution.RefundTransactionId!, refundIntent, ct);
                if (result.Status != PaymentVerificationStatus.Verified)
                {
                    var waiting = result.Status == PaymentVerificationStatus.AwaitingFinality;
                    var update = Builders<RelayPaymentIntentDocument>.Update.Set(x => x.Status, waiting ? "applying" : "reconciliation_required")
                        .Set(x => x.RecoveryMessage, waiting ? "Refund proof is awaiting finality; no refund has been marked complete."
                            : "No valid refund from the original recipient to the payer was found. Review the proof and use a new resolution ID if corrected.");
                    if (!waiting) update = update.Set(x => x.ActiveResolutionId, null);
                    await db.PaymentIntents.UpdateOneAsync(x => x.Id == intent.Id && x.ActiveResolutionId == resolution.Id, update, cancellationToken: ct);
                    return await ResponseAsync(await IntentAsync(intent.Id, ct), intent.SubmittedTransactionId!, ct);
                }
                await ClaimTransactionAsync(resolution.RefundTransactionId!, resolution.Id, "refund", ct);
                await db.PaymentTransactions.UpdateOneAsync(new BsonDocument("_id", resolution.RefundTransactionId),
                    new BsonDocument("$set", new BsonDocument { { "verified", true }, { "amountDatoshis", resolution.AmountDatoshis },
                        { "actualRefundGasDatoshis", result.GasDatoshis }, { "resolvedGasDatoshis", Math.Min(resolution.AmountDatoshis, result.GasDatoshis) },
                        { "blockIndex", result.BlockIndex } }), cancellationToken: ct);
                proof = await db.PaymentTransactions.Find(new BsonDocument("_id", resolution.RefundTransactionId)).SingleAsync(ct);
            }
            unapplied = resolution.AmountDatoshis - proof["resolvedGasDatoshis"].ToInt64();
        }
        else
        {
            var quote = resolution.ReplacementQuote!;
            var original = await db.Payments.Find(x => x.Id == intent.SubmittedTransactionId).SingleAsync(ct);
            var synthetic = new RelayPaymentDocument { Id = resolution.ReplacementDecisionId!, IntentId = quote.Id, ProjectId = intent.ProjectId,
                SenderScriptHash = original.SenderScriptHash, RecipientScriptHash = original.RecipientScriptHash,
                GasDatoshis = resolution.AmountDatoshis, Terms = quote.Terms, VerifiedAt = resolution.CreatedAt };
            var outcome = await Applications.ApplyAsync(quote, synthetic, ct);
            if (!outcome.Complete)
            {
                await SetStateAsync(intent.Id, outcome.Conflict ? "reconciliation_required" : "applying", outcome.Message, null, ct);
                return await ResponseAsync(await IntentAsync(intent.Id, ct), intent.SubmittedTransactionId!, ct);
            }
            unapplied = outcome.UnappliedGasDatoshis;
            applicationId = synthetic.Id;
            var end = outcome.Decision.Allocations.Where(x => x.Status == "accepted").Select(x => (DateTime?)x.EndsAt).Max();
            if (end is not null)
                await db.Payments.UpdateOneAsync(x => x.Id == intent.SubmittedTransactionId && (x.EntitlementEndsAt == null || x.EntitlementEndsAt < end),
                    Builders<RelayPaymentDocument>.Update.Set(x => x.EntitlementEndsAt, end), cancellationToken: ct);
        }
        await db.PaymentIntents.UpdateOneAsync(x => x.Id == intent.Id && x.ActiveResolutionId == resolution.Id,
            Builders<RelayPaymentIntentDocument>.Update.Set(x => x.Status, unapplied == 0 ? "resolved" : "reconciliation_required")
                .Set(x => x.UnappliedGasDatoshis, unapplied).Set(x => x.ActiveResolutionId, null)
                .Set(x => x.ApplicationDecisionId, applicationId).AddToSet(x => x.CompletedResolutions, resolution.Id)
                .Set(x => x.RecoveryMessage, resolution.Action == "refund" ? unapplied == 0 ? "Refund transaction verified and recorded." : "Partial refund recorded; the remaining unapplied balance is still owed."
                    : "Owner-approved replacement applied without another transfer."), cancellationToken: ct);
        if (unapplied == 0) await Admission.ReleaseAsync(intent.ProjectId, intent.Id, ct);
        return await ResponseAsync(await IntentAsync(intent.Id, ct), intent.SubmittedTransactionId!, ct);
    }
}
