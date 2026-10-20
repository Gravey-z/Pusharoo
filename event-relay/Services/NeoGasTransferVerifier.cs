using System.Globalization;
using System.Text.Json;
using static Pusharoo.EventRelay.Services.NeoPaymentEncoding;

namespace Pusharoo.EventRelay.Services;

public static class NeoGasTransferVerifier
{
    private const string GasContractHash = "0xd2a4cff31913016155e38e474a2c06d08be276cf";

    public static bool TryVerify(
        JsonElement log,
        string payer,
        string recipient,
        long minimum,
        out long amount
    )
    {
        amount = 0;
        long total = 0;
        var hasRequiredTransfer = false;

        if (
            !log.TryGetProperty("executions", out var executions)
            || executions.ValueKind != JsonValueKind.Array
        )
            return false;

        foreach (var execution in executions.EnumerateArray())
        {
            if (
                !execution.TryGetProperty("vmstate", out var vmState)
                || !string.Equals(vmState.GetString(), "HALT", StringComparison.OrdinalIgnoreCase)
            )
                return false;

            if (
                !execution.TryGetProperty("notifications", out var notifications)
                || notifications.ValueKind != JsonValueKind.Array
            )
                continue;

            foreach (var notification in notifications.EnumerateArray())
            {
                if (
                    !notification.TryGetProperty("contract", out var contract)
                    || !string.Equals(
                        NormalizeHash(contract.GetString()),
                        GasContractHash,
                        StringComparison.Ordinal
                    )
                )
                    continue;

                if (
                    !notification.TryGetProperty("eventname", out var eventName)
                    || !string.Equals(eventName.GetString(), "Transfer", StringComparison.Ordinal)
                )
                    continue;

                if (
                    !notification.TryGetProperty("state", out var state)
                    || !TryGetValues(state, out var values)
                    || values.Length < 3
                )
                    continue;

                if (
                    !TryGetScriptHash(values[0], out var sender)
                    || !TryGetScriptHash(values[1], out var receiver)
                    || !TryGetInteger(values[2], out var transferAmount)
                )
                    continue;

                if (
                    string.Equals(sender, payer, StringComparison.Ordinal)
                    && string.Equals(receiver, recipient, StringComparison.Ordinal)
                    && transferAmount > 0
                )
                {
                    hasRequiredTransfer |= transferAmount >= minimum;
                    try
                    {
                        total = checked(total + transferAmount);
                    }
                    catch (OverflowException)
                    {
                        return false;
                    }
                }
            }
        }

        if (!hasRequiredTransfer)
            return false;
        amount = total;
        return true;
    }

    private static bool TryGetValues(JsonElement state, out JsonElement[] values)
    {
        values = [];

        if (state.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            values = value.EnumerateArray().ToArray();

            return true;
        }

        return false;
    }

    private static bool TryGetInteger(JsonElement value, out long result)
    {
        result = 0;

        if (value.TryGetProperty("value", out var nested))
            value = nested;

        return value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt64(out result)
            : long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result
            );
    }

    private static bool TryGetScriptHash(JsonElement value, out string hash)
    {
        hash = string.Empty;

        if (value.TryGetProperty("value", out var nested))
            value = nested;

        var text = value.GetString();

        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hash = NormalizeHash(text);

            return IsScriptHash(hash);
        }

        try
        {
            var bytes = Convert.FromBase64String(text);

            if (bytes.Length != 20)
                return false;

            Array.Reverse(bytes);
            hash = "0x" + Convert.ToHexString(bytes).ToLowerInvariant();

            return true;
        }
        catch
        {
            return false;
        }
    }
}
