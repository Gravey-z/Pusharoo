using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using backend.Options;
using Microsoft.Extensions.Options;
using Neo;

namespace backend.Services;

public sealed class FaucetRpcService(
    NeoRpcClient rpc,
    IOptions<NeoRpcOptions> neoOptions,
    IOptions<FaucetOptions> faucetOptions)
{
    private const string TestnetName = "testnet";
    private readonly NeoNetworkRpcOptions? network = neoOptions.Value.Networks.GetValueOrDefault(TestnetName);
    private readonly FaucetOptions faucet = faucetOptions.Value;

    public async Task<FaucetAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (network is null || string.IsNullOrWhiteSpace(network.Endpoint) || !IsScriptHash(faucet.ContractHash))
            return FaucetAvailability.Offline("Testnet faucet is not configured.");

        try
        {
            var version = await rpc.SendAsync(network.Endpoint, "getversion", [], cancellationToken);
            var actualMagic = version.GetProperty("protocol").GetProperty("network").GetInt64();
            if (actualMagic != faucet.NetworkMagic)
                return FaucetAvailability.Offline("Configured RPC is not Neo N3 testnet.");

            await rpc.SendAsync(network.Endpoint, "getcontractstate", [faucet.ContractHash], cancellationToken);
            return FaucetAvailability.Online;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException)
        {
            return FaucetAvailability.Offline("Testnet faucet is temporarily unavailable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FaucetAvailability.Offline("Testnet faucet RPC timed out.");
        }
    }

    public async Task<JsonElement> InvokeAsync(string method, string? scriptHash, CancellationToken cancellationToken)
    {
        var availability = await GetAvailabilityAsync(cancellationToken);
        if (!availability.Available || network is null)
            throw new FaucetUnavailableException(availability.Reason ?? "Testnet faucet is unavailable.");

        var arguments = scriptHash is null
            ? Array.Empty<object>()
            : [new { type = "Hash160", value = scriptHash }];
        return await rpc.SendAsync(network.Endpoint, "invokefunction", [faucet.ContractHash, method, arguments], cancellationToken);
    }

    public string ContractHash => faucet.ContractHash;

    public string ExplorerTransactionUrl(string transactionHash) => $"https://dora.coz.io/transaction/neo3/testnet/{Uri.EscapeDataString(transactionHash)}";

    public static string StackValue(JsonElement item)
    {
        if (item.TryGetProperty("value", out var value))
        {
            if (item.GetProperty("type").GetString() is "ByteString" or "Buffer")
                return Encoding.UTF8.GetString(Convert.FromBase64String(value.GetString() ?? string.Empty));
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.GetRawText()
            };
        }
        return string.Empty;
    }

    public static BigInteger StackInteger(JsonElement item)
    {
        var type = item.GetProperty("type").GetString();
        var raw = item.GetProperty("value").GetString() ?? string.Empty;
        if (type == "Integer" && BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number;
        if (type is "ByteString" or "Buffer")
            return new BigInteger(Convert.FromBase64String(raw), isUnsigned: false, isBigEndian: false);
        throw new FormatException($"Expected a Neo integer stack item, got {type}.");
    }

    public static JsonElement StackArray(JsonElement result)
    {
        var stack = result.GetProperty("stack");
        if (stack.ValueKind != JsonValueKind.Array || stack.GetArrayLength() != 1
            || stack[0].GetProperty("type").GetString() != "Array"
            || !stack[0].TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new FormatException("Neo returned an invalid faucet status array.");
        return items;
    }

    public static UInt160? StackHash160(JsonElement item)
    {
        var type = item.GetProperty("type").GetString();
        if (type == "Any") return null;

        var value = item.GetProperty("value").GetString() ?? string.Empty;
        if (type == "Hash160") return UInt160.Parse(value);
        if (type is not ("ByteString" or "Buffer"))
            throw new FormatException($"Expected a Neo Hash160 stack item, got {type}.");

        var bytes = Convert.FromBase64String(value);
        if (bytes.Length != 20) throw new FormatException("Neo Hash160 stack item must contain 20 bytes.");
        return new UInt160(bytes);
    }

    private static bool IsScriptHash(string value)
    {
        var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return normalized.Length == 40 && normalized.All(Uri.IsHexDigit);
    }
}

public sealed record FaucetAvailability(bool Available, string? Reason)
{
    public static FaucetAvailability Online { get; } = new(true, null);
    public static FaucetAvailability Offline(string reason) => new(false, reason);
}

public sealed class FaucetUnavailableException(string message) : Exception(message);
