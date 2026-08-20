using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayPaymentService(
    MongoDbContext db,
    NeoRpcClient neoRpc,
    RelayEntitlementService entitlements,
    IOptions<EventRelayOptions> relayOptions,
    IOptions<NeoRpcOptions> neoRpcOptions)
{
    private const string GasContractHash = "0xd2a4cff31913016155e38e474a2c06d08be276cf";
    private readonly EventRelayOptions settings = relayOptions.Value;
    private readonly NeoRpcOptions neoSettings = neoRpcOptions.Value;

    public bool IsMainNet => string.Equals(neoSettings.Network, "neo3:mainnet", StringComparison.Ordinal);

    public async Task<PaymentIntentResponse> CreateIntentAsync(string projectId, WalletSignatureRequest signature, CancellationToken ct)
    {
        var recipientScriptHash = GetRecipientScriptHash();
        var now = DateTime.UtcNow;
        var intent = new RelayPaymentIntentDocument
        {
            Id = Guid.NewGuid().ToString("n"), ProjectId = projectId.Trim(), PayerAddress = signature.Address.Trim(),
            PayerScriptHash = NormalizeHash(signature.ScriptHash), RecipientAddress = settings.PaymentRecipientAddress.Trim(),
            RecipientScriptHash = recipientScriptHash, RequiredGasDatoshis = settings.PaidPlanGasDatoshis,
            CreatedAt = now, ExpiresAt = now.AddMinutes(settings.PaymentIntentMinutes)
        };
        await db.PaymentIntents.InsertOneAsync(intent, cancellationToken: ct);
        return ToResponse(intent);
    }

    public async Task<PaymentResponse> ConfirmAsync(string projectId, string intentId, string transactionId, CancellationToken ct)
    {
        EnsureAvailable();
        var intent = await db.PaymentIntents.Find(x => x.Id == intentId && x.ProjectId == projectId).FirstOrDefaultAsync(ct);
        if (intent is null) throw new PaymentValidationException("Payment intent was not found.", 404);
        if (intent.Status == "confirmed") return new PaymentResponse(intent.ConfirmedTransactionId ?? string.Empty, intent.Id, "confirmed", await PaidUntilAsync(projectId, ct));
        if (intent.Status is not ("pending" or "awaiting_finality")) throw new PaymentValidationException("This payment intent is no longer usable.", 409);
        if (intent.ExpiresAt <= DateTime.UtcNow)
        {
            await db.PaymentIntents.UpdateOneAsync(x => x.Id == intent.Id, Builders<RelayPaymentIntentDocument>.Update.Set(x => x.Status, "expired"), cancellationToken: ct);
            throw new PaymentValidationException("Payment intent expired. Create a new one before sending GAS.", 409);
        }

        var normalizedTransactionId = NormalizeTransactionId(transactionId);
        if (!string.IsNullOrWhiteSpace(intent.SubmittedTransactionId)
            && !string.Equals(intent.SubmittedTransactionId, normalizedTransactionId, StringComparison.Ordinal))
            throw new PaymentValidationException("This payment intent is already associated with another transaction.", 409);
        try
        {
            var association = await db.PaymentIntents.UpdateOneAsync(
                Builders<RelayPaymentIntentDocument>.Filter.And(
                    Builders<RelayPaymentIntentDocument>.Filter.Eq(item => item.Id, intent.Id),
                    Builders<RelayPaymentIntentDocument>.Filter.Or(
                        Builders<RelayPaymentIntentDocument>.Filter.Eq(item => item.SubmittedTransactionId, null),
                        Builders<RelayPaymentIntentDocument>.Filter.Eq(item => item.SubmittedTransactionId, normalizedTransactionId))),
                Builders<RelayPaymentIntentDocument>.Update
                    .Set(item => item.SubmittedTransactionId, normalizedTransactionId)
                    .Set(item => item.SubmittedAt, DateTime.UtcNow),
                cancellationToken: ct);
            if (association.MatchedCount != 1)
                throw new PaymentValidationException("This payment intent is already associated with another transaction.", 409);
        }
        catch (MongoWriteException)
        {
            throw new PaymentValidationException("This transaction is already associated with another payment intent.", 409);
        }
        var claimed = await db.Payments.Find(x => x.Id == normalizedTransactionId).FirstOrDefaultAsync(ct);
        if (claimed is not null)
        {
            if (!string.Equals(claimed.IntentId, intent.Id, StringComparison.Ordinal)) throw new PaymentValidationException("This transaction has already been used for another payment.", 409);
            var existingEntitlements = await entitlements.GrantPaidAccessAsync(projectId, claimed.Id, ct);
            var existingPaidUntil = existingEntitlements.First(x => x.Network == "neo3:mainnet").PeriodEndsAt;
            await db.PaymentIntents.UpdateOneAsync(x => x.Id == intent.Id, Builders<RelayPaymentIntentDocument>.Update.Set(x => x.Status, "confirmed").Set(x => x.ConfirmedTransactionId, claimed.Id).Set(x => x.ConfirmedAt, DateTime.UtcNow), cancellationToken: ct);
            return new PaymentResponse(claimed.Id, intent.Id, "confirmed", existingPaidUntil);
        }

        var raw = await neoRpc.GetRawTransactionAsync(normalizedTransactionId, ct);
        var confirmations = ReadUInt(raw, "confirmations");
        if (confirmations < Math.Max(1, settings.PaymentConfirmationBlocks))
        {
            await db.PaymentIntents.UpdateOneAsync(item => item.Id == intent.Id, Builders<RelayPaymentIntentDocument>.Update.Set(item => item.Status, "awaiting_finality"), cancellationToken: ct);
            return new PaymentResponse(normalizedTransactionId, intent.Id, "awaiting_finality", null, $"Waiting for {Math.Max(1, settings.PaymentConfirmationBlocks)} MainNet confirmations.");
        }
        var log = await neoRpc.GetApplicationLogAsync(normalizedTransactionId, ct);
        if (!TryFindVerifiedGasTransfer(log, intent.PayerScriptHash, intent.RecipientScriptHash, intent.RequiredGasDatoshis, out var amount))
        {
            await db.PaymentIntents.UpdateOneAsync(item => item.Id == intent.Id, Builders<RelayPaymentIntentDocument>.Update.Set(item => item.Status, "rejected").Set(item => item.RejectionReason, "The transaction did not contain a successful required GAS transfer."), cancellationToken: ct);
            throw new PaymentValidationException("The confirmed transaction does not contain the required GAS transfer from this wallet.", 409);
        }

        var payment = new RelayPaymentDocument
        {
            Id = normalizedTransactionId, IntentId = intent.Id, ProjectId = intent.ProjectId, SenderScriptHash = intent.PayerScriptHash,
            RecipientScriptHash = intent.RecipientScriptHash, GasDatoshis = amount, Network = "neo3:mainnet",
            BlockIndex = ReadUInt(raw, "blockindex", "blockIndex"), VerifiedAt = DateTime.UtcNow
        };
        try { await db.Payments.InsertOneAsync(payment, cancellationToken: ct); }
        catch (MongoWriteException) { return await ConfirmAsync(projectId, intentId, normalizedTransactionId, ct); }

        var granted = await entitlements.GrantPaidAccessAsync(projectId, payment.Id, ct);
        var paidUntil = granted.First(x => x.Network == "neo3:mainnet").PeriodEndsAt;
        await db.Payments.UpdateOneAsync(x => x.Id == payment.Id, Builders<RelayPaymentDocument>.Update.Set(x => x.EntitlementEndsAt, paidUntil), cancellationToken: ct);
        await db.PaymentIntents.UpdateOneAsync(x => x.Id == intent.Id, Builders<RelayPaymentIntentDocument>.Update.Set(x => x.Status, "confirmed").Set(x => x.ConfirmedTransactionId, payment.Id).Set(x => x.ConfirmedAt, DateTime.UtcNow), cancellationToken: ct);
        return new PaymentResponse(payment.Id, intent.Id, "confirmed", paidUntil);
    }

    public async Task<PaymentHistoryResponse> HistoryAsync(string projectId, CancellationToken ct)
    {
        await db.PaymentIntents.UpdateManyAsync(
            item => item.ProjectId == projectId && (item.Status == "pending" || item.Status == "awaiting_finality") && item.ExpiresAt <= DateTime.UtcNow,
            Builders<RelayPaymentIntentDocument>.Update.Set(item => item.Status, "expired"),
            cancellationToken: ct);
        var payments = await db.Payments.Find(x => x.ProjectId == projectId).SortByDescending(x => x.VerifiedAt).Limit(25).ToListAsync(ct);
        var history = await db.EntitlementHistory.Find(x => x.ProjectId == projectId).SortByDescending(x => x.CreatedAt).Limit(50).ToListAsync(ct);
        var pending = await db.PaymentIntents.Find(x => x.ProjectId == projectId && (x.Status == "pending" || x.Status == "awaiting_finality")).SortByDescending(x => x.CreatedAt).Limit(10).ToListAsync(ct);
        return new PaymentHistoryResponse(
            payments.Select(x => new PaymentResponse(x.Id, x.IntentId, "confirmed", x.EntitlementEndsAt)).ToArray(),
            history.Select(x => new EntitlementHistoryResponse(x.Network, x.Plan, x.PeriodStart, x.PeriodEndsAt, x.GraceEndsAt)).ToArray(),
            pending.Select(ToResponse).ToArray());
    }

    private async Task<DateTime?> PaidUntilAsync(string projectId, CancellationToken ct) => (await entitlements.GetAsync(projectId, "neo3:mainnet", ct)).PaidUntil;

    private void EnsureAvailable()
    {
        if (!IsMainNet) throw new PaymentValidationException("Payments are available from the MainNet relay only.", 404);
        if (!TryGetScriptHashFromAddress(settings.PaymentRecipientAddress, out _) || settings.PaidPlanGasDatoshis <= 0)
            throw new PaymentValidationException("Paid Relay is not configured yet. Set the MainNet payment recipient before launching payments.", 503);
    }

    private string GetRecipientScriptHash()
    {
        EnsureAvailable();
        return TryGetScriptHashFromAddress(settings.PaymentRecipientAddress, out var scriptHash)
            ? scriptHash
            : throw new PaymentValidationException("Paid Relay is not configured yet. Set the MainNet payment recipient before launching payments.", 503);
    }

    private static PaymentIntentResponse ToResponse(RelayPaymentIntentDocument intent) => new(intent.Id, intent.ProjectId, intent.Network, intent.RecipientAddress, intent.RecipientScriptHash, intent.RequiredGasDatoshis, intent.Status, intent.CreatedAt, intent.ExpiresAt, intent.ConfirmedTransactionId, intent.SubmittedTransactionId);
    private static string NormalizeHash(string? hash) { var value = hash?.Trim() ?? string.Empty; return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? $"0x{value[2..].ToLowerInvariant()}" : $"0x{value.ToLowerInvariant()}"; }
    private static bool IsScriptHash(string? hash) => NormalizeHash(hash).Length == 42 && NormalizeHash(hash)[2..].All(Uri.IsHexDigit);
    private static bool TryGetScriptHashFromAddress(string address, out string scriptHash)
    {
        scriptHash = string.Empty;
        try
        {
            var decoded = Base58Decode(address.Trim());
            if (decoded.Length != 25 || decoded[0] != 0x35) return false;
            var checksum = SHA256.HashData(SHA256.HashData(decoded[..21]))[..4];
            if (!checksum.SequenceEqual(decoded[21..])) return false;
            var bytes = decoded[1..21];
            Array.Reverse(bytes);
            scriptHash = "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
            return true;
        }
        catch { return false; }
    }
    private static byte[] Base58Decode(string value)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        BigInteger number = BigInteger.Zero;
        foreach (var character in value)
        {
            var index = alphabet.IndexOf(character);
            if (index < 0) throw new FormatException();
            number = number * 58 + index;
        }
        var encoded = number.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = value.TakeWhile(character => character == '1').Count();
        return Enumerable.Repeat((byte)0, leadingZeros).Concat(encoded).ToArray();
    }
    private static string NormalizeTransactionId(string? id) { var value = id?.Trim() ?? string.Empty; if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..]; if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new PaymentValidationException("Transaction ID must be a 32-byte hexadecimal hash.", 400); return "0x" + value.ToLowerInvariant(); }
    private static uint ReadUInt(JsonElement value, params string[] names) { foreach (var name in names) if (value.TryGetProperty(name, out var item)) { if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out var number)) return number; if (uint.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number; } return 0; }
    private static bool TryFindVerifiedGasTransfer(JsonElement log, string payer, string recipient, long minimum, out long amount)
    {
        amount = 0;
        if (!log.TryGetProperty("executions", out var executions) || executions.ValueKind != JsonValueKind.Array) return false;
        foreach (var execution in executions.EnumerateArray())
        {
            if (!execution.TryGetProperty("vmstate", out var vmState) || !string.Equals(vmState.GetString(), "HALT", StringComparison.OrdinalIgnoreCase)) return false;
            if (!execution.TryGetProperty("notifications", out var notifications) || notifications.ValueKind != JsonValueKind.Array) continue;
            foreach (var notification in notifications.EnumerateArray())
            {
                if (!notification.TryGetProperty("contract", out var contract) || !string.Equals(NormalizeHash(contract.GetString()), GasContractHash, StringComparison.Ordinal)) continue;
                if (!notification.TryGetProperty("eventname", out var eventName) || !string.Equals(eventName.GetString(), "Transfer", StringComparison.Ordinal)) continue;
                if (!notification.TryGetProperty("state", out var state) || !TryGetValues(state, out var values) || values.Length < 3) continue;
                if (!TryGetScriptHash(values[0], out var sender) || !TryGetScriptHash(values[1], out var receiver) || !TryGetInteger(values[2], out var transferAmount)) continue;
                if (string.Equals(sender, payer, StringComparison.Ordinal) && string.Equals(receiver, recipient, StringComparison.Ordinal) && transferAmount >= minimum) { amount = transferAmount; return true; }
            }
        }
        return false;
    }
    private static bool TryGetValues(JsonElement state, out JsonElement[] values) { values = []; if (state.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array) { values = value.EnumerateArray().ToArray(); return true; } return false; }
    private static bool TryGetInteger(JsonElement value, out long result) { result = 0; if (value.TryGetProperty("value", out var nested)) value = nested; return value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out result) : long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result); }
    private static bool TryGetScriptHash(JsonElement value, out string hash)
    {
        hash = string.Empty; if (value.TryGetProperty("value", out var nested)) value = nested; var text = value.GetString(); if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { hash = NormalizeHash(text); return IsScriptHash(hash); }
        try { var bytes = Convert.FromBase64String(text); if (bytes.Length != 20) return false; Array.Reverse(bytes); hash = "0x" + Convert.ToHexString(bytes).ToLowerInvariant(); return true; } catch { return false; }
    }
}

public sealed class PaymentValidationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
