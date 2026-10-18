using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayPaymentVerifier(NeoRpcClient neoRpc, IOptions<EventRelayOptions> options)
{
    public async Task<PaymentVerificationResult> VerifyAsync(
        string transactionId,
        RelayPaymentIntentDocument intent,
        CancellationToken ct
    )
    {
        var raw = await neoRpc.GetRawTransactionAsync(transactionId, ct);
        var confirmations = ReadUInt(raw, "confirmations");

        if (confirmations < Math.Max(1, options.Value.PaymentConfirmationBlocks))
            return new PaymentVerificationResult(PaymentVerificationStatus.AwaitingFinality);

        var log = await neoRpc.GetApplicationLogAsync(transactionId, ct);

        if (
            !NeoGasTransferVerifier.TryVerify(
                log,
                intent.PayerScriptHash,
                intent.RecipientScriptHash,
                intent.RequiredGasDatoshis,
                out var amount
            )
        )
            return new PaymentVerificationResult(PaymentVerificationStatus.Rejected);

        return new PaymentVerificationResult(
            PaymentVerificationStatus.Verified,
            ReadUInt(raw, "blockindex", "blockIndex"),
            amount
        );
    }

    private static uint ReadUInt(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (value.TryGetProperty(name, out var item))
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out var number))
                    return number;

                if (
                    uint.TryParse(
                        item.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out number
                    )
                )
                    return number;
            }

        return 0;
    }
}
