using System.Text.Json;
using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;

namespace backend.Services;

public sealed class NeoDeploymentVerificationService(
    NeoRpcClient neoRpc,
    IOptions<NeoRpcOptions> options)
{
    private readonly NeoRpcOptions options = options.Value;

    public async Task<NeoDeploymentVerificationResult> VerifyAsync(
        ProjectDocument project,
        IReadOnlyList<DeploymentDocument> existingDeployments,
        CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TransactionId))
        {
            return Fail("Transaction ID is required for deployment verification.");
        }

        if (string.IsNullOrWhiteSpace(request.ContractHash))
        {
            return Fail("Contract hash is required for deployment verification.");
        }

        if (string.IsNullOrWhiteSpace(project.CreatedByWalletScriptHash))
        {
            return Fail("Project creator script hash is missing; deployment verification cannot continue.");
        }

        if (!options.Networks.TryGetValue(request.Network.Trim(), out var networkOptions)
            || string.IsNullOrWhiteSpace(networkOptions.Endpoint))
        {
            return Fail($"No Neo RPC endpoint is configured for {request.Network}.");
        }

        var expectedAction = HasExistingNetworkDeployment(existingDeployments, request.Network)
            ? "Update"
            : "Deploy";

        try
        {
            var transaction = await neoRpc.SendAsync(
                networkOptions.Endpoint,
                "getrawtransaction",
                [request.TransactionId.Trim(), 1],
                cancellationToken);
            if (!HasSigner(transaction, project.CreatedByWalletScriptHash))
            {
                return Fail("Deployment transaction was not signed by the project creator.");
            }

            var applicationLog = await neoRpc.SendAsync(
                networkOptions.Endpoint,
                "getapplicationlog",
                [request.TransactionId.Trim()],
                cancellationToken);

            var execution = FirstExecution(applicationLog);
            var vmState = GetString(execution, "vmstate") ?? GetString(execution, "state") ?? string.Empty;
            if (!string.Equals(vmState, "HALT", StringComparison.Ordinal))
            {
                var exception = GetString(execution, "exception");
                return Fail($"Deployment transaction finished with {Fallback(vmState, "UNKNOWN")}{(string.IsNullOrWhiteSpace(exception) ? "." : $": {exception}")}");
            }

            var notification = FindContractManagementNotification(
                execution,
                networkOptions.ContractManagementHash,
                expectedAction);
            if (notification is null)
            {
                return Fail($"Deployment transaction does not contain the expected ContractManagement {expectedAction} notification.");
            }

            var notificationContractHash = FindHashInStackItem(GetProperty(notification.Value, "state"))
                ?? FindHashInStackItems(GetArray(execution, "stack"));
            if (string.IsNullOrWhiteSpace(notificationContractHash))
            {
                return Fail($"Deployment transaction halted, but the {expectedAction} notification did not include a contract hash.");
            }

            if (!HashesMatch(notificationContractHash, request.ContractHash))
            {
                return Fail("Deployment transaction contract hash does not match the requested deployment record.");
            }

            return NeoDeploymentVerificationResult.Valid;
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Could not verify deployment against Neo RPC: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Fail($"Could not verify deployment against Neo RPC: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return Fail($"Neo RPC returned an unexpected deployment verification response: {ex.Message}");
        }
    }

    private static bool HasExistingNetworkDeployment(
        IReadOnlyList<DeploymentDocument> deployments,
        string network)
    {
        return deployments.Any(deployment =>
            string.Equals(deployment.Network, network.Trim(), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(deployment.ContractHash));
    }

    private static bool HasSigner(JsonElement transaction, string expectedScriptHash)
    {
        foreach (var signer in GetArray(transaction, "signers"))
        {
            var signerAccount = GetString(signer, "account");
            if (!string.IsNullOrWhiteSpace(signerAccount) && HashesMatch(signerAccount, expectedScriptHash))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonElement FirstExecution(JsonElement applicationLog)
    {
        var executions = GetArray(applicationLog, "executions");

        return executions.FirstOrDefault();
    }

    private static JsonElement? FindContractManagementNotification(
        JsonElement execution,
        string contractManagementHash,
        string eventName)
    {
        foreach (var notification in GetArray(execution, "notifications"))
        {
            var notificationContract = GetString(notification, "contract")
                ?? GetString(notification, "scripthash");
            var notificationEvent = GetString(notification, "eventname")
                ?? GetString(notification, "eventName");

            if (!string.IsNullOrWhiteSpace(notificationContract)
                && HashesMatch(notificationContract, contractManagementHash)
                && string.Equals(notificationEvent, eventName, StringComparison.Ordinal))
            {
                return notification;
            }
        }

        return null;
    }

    private static string? FindHashInStackItems(IEnumerable<JsonElement> items)
    {
        foreach (var item in items)
        {
            var hash = FindHashInStackItem(item);
            if (!string.IsNullOrWhiteSpace(hash))
            {
                return hash;
            }
        }

        return null;
    }

    private static string? FindHashInStackItem(JsonElement? item)
    {
        if (item is null || item.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var type = GetString(item.Value, "type");
        var value = GetProperty(item.Value, "value");
        if (string.Equals(type, "Hash160", StringComparison.Ordinal) && value?.ValueKind == JsonValueKind.String)
        {
            return value.Value.GetString();
        }

        if ((string.Equals(type, "ByteString", StringComparison.Ordinal) || string.Equals(type, "Buffer", StringComparison.Ordinal))
            && value?.ValueKind == JsonValueKind.String)
        {
            return Base64StackValueToHash(value.Value.GetString());
        }

        if (value?.ValueKind == JsonValueKind.Array)
        {
            return FindHashInStackItems(value.Value.EnumerateArray());
        }

        return null;
    }

    private static string? Base64StackValueToHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length != 20)
            {
                return null;
            }

            Array.Reverse(bytes);

            return $"0x{Convert.ToHexString(bytes).ToLowerInvariant()}";
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> GetArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Array)
        {
            return property.EnumerateArray();
        }

        return [];
    }

    private static JsonElement? GetProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static bool HashesMatch(string? left, string? right)
    {
        var normalizedLeft = NormalizeHash(left);
        var normalizedRight = NormalizeHash(right);
        if (normalizedLeft is null || normalizedRight is null)
        {
            return false;
        }

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal)
            || string.Equals(ReverseHash(normalizedLeft), normalizedRight, StringComparison.Ordinal);
    }

    private static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return normalized.Length == 40 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToLowerInvariant()
            : null;
    }

    private static string ReverseHash(string hash)
    {
        var bytes = Convert.FromHexString(hash);
        Array.Reverse(bytes);

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Fallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static NeoDeploymentVerificationResult Fail(string error)
    {
        return new NeoDeploymentVerificationResult(false, error);
    }
}

public sealed record NeoDeploymentVerificationResult(bool IsValid, string Error)
{
    public static NeoDeploymentVerificationResult Valid { get; } = new(true, string.Empty);
}
