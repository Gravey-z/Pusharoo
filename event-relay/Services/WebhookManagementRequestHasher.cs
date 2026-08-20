using System.Security.Cryptography;
using System.Text;
using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public static class WebhookManagementRequestHasher
{
    public static string Hash(
        string projectId,
        string operation,
        string? subscriptionId = null,
        string? name = null,
        string? contractHash = null,
        string? network = null,
        string? eventName = null,
        string? webhookUrl = null,
        string? secret = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool? isEnabled = null)
    {
        var payload = string.Join('\n', new[]
        {
            $"Project ID: {Normalize(projectId)}",
            $"Operation: {Normalize(operation)}",
            $"Subscription ID: {Normalize(subscriptionId)}",
            $"Name: {Normalize(name)}",
            $"Contract hash: {Normalize(contractHash).ToLowerInvariant()}",
            $"Network: {Normalize(network)}",
            $"Event name: {Normalize(eventName)}",
            $"Webhook URL: {Normalize(webhookUrl)}",
            $"Enabled: {isEnabled?.ToString().ToLowerInvariant() ?? string.Empty}",
            $"Secret SHA-256: {Sha256Hex(Normalize(secret))}",
            $"Headers SHA-256: {Sha256Hex(NormalizeHeaders(headers))}"
        });

        return Sha256Hex(payload);
    }

    public static string HashCreate(string projectId, CreateSubscriptionRequest request)
    {
        return Hash(
            projectId,
            "subscriptions.create",
            name: request.Name,
            contractHash: request.ContractHash,
            network: request.Network,
            eventName: request.EventName,
            webhookUrl: request.WebhookUrl,
            secret: request.Secret,
            headers: request.Headers,
            isEnabled: request.IsEnabled);
    }

    public static string HashUpdate(string projectId, string subscriptionId, UpdateSubscriptionRequest request)
    {
        return Hash(
            projectId,
            "subscriptions.update",
            subscriptionId,
            request.Name,
            request.ContractHash,
            request.Network,
            request.EventName,
            request.WebhookUrl,
            request.Secret,
            request.Headers,
            request.IsEnabled);
    }

    public static string HashPaymentIntent(string projectId) => Sha256Hex(string.Join('\n', new[]
    {
        $"Project ID: {Normalize(projectId)}",
        "Operation: payments.create"
    }));

    public static string HashPaymentConfirmation(string projectId, string intentId, string transactionId) => Sha256Hex(string.Join('\n', new[]
    {
        $"Project ID: {Normalize(projectId)}",
        "Operation: payments.confirm",
        $"Payment intent ID: {Normalize(intentId)}",
        $"Transaction ID: {Normalize(transactionId).ToLowerInvariant()}"
    }));

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            '\n',
            headers
                .Select(header => $"{Normalize(header.Key).ToLowerInvariant()}:{Normalize(header.Value)}")
                .Order(StringComparer.Ordinal));
    }

    private static string Sha256Hex(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
