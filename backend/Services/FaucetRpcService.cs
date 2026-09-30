using System.Globalization;
using System.Numerics;
using System.Text.Json;
using backend.Options;
using Microsoft.Extensions.Options;

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
        var raw = StackValue(item);
        if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;
        var bytes = Convert.FromHexString(raw);
        return new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
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
