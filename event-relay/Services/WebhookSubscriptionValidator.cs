using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookSubscriptionValidator(
    WebhookDestinationValidator destinationValidator,
    IOptions<NeoRpcOptions> neoRpcOptions
)
{
    private static readonly Regex HeaderNamePattern = new(
        "^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,64}$",
        RegexOptions.Compiled
    );

    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Connection",
        "Content-Length",
        "Cookie",
        "Host",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Te",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "X-Pusharoo-Delivery",
        "X-Pusharoo-Event",
        "X-Pusharoo-Signature",
        "X-Pusharoo-Event-Id",
        "X-Pusharoo-Payload-Version",
    };

    public async Task<string?> ValidateAsync(
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!WebhookPayloadContract.IsSupported(request.PayloadVersion))
            return "Only payload version 2 is supported.";

        return await ValidateSubscriptionAsync(
            request.Name,
            request.ContractHash,
            request.Network,
            request.WebhookUrl,
            request.Headers,
            cancellationToken
        );
    }

    public async Task<string?> ValidateAsync(
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!WebhookPayloadContract.IsSupported(request.PayloadVersion))
            return "Only payload version 2 is supported.";

        return await ValidateSubscriptionAsync(
            request.Name,
            request.ContractHash,
            request.Network,
            request.WebhookUrl,
            request.Headers,
            cancellationToken
        );
    }

    private async Task<string?> ValidateSubscriptionAsync(
        string name,
        string contractHash,
        string network,
        string webhookUrl,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
        {
            return "Webhook name is required and must be 120 characters or fewer.";
        }

        if (!IsContractHash(contractHash))
        {
            return "Contract hash must be a 20-byte hexadecimal script hash.";
        }

        if (!string.Equals(network.Trim(), neoRpcOptions.Value.Network, StringComparison.Ordinal))
        {
            return $"Pusharoo Relay currently monitors {neoRpcOptions.Value.Network}.";
        }

        var destination = await destinationValidator.ValidateAsync(webhookUrl, cancellationToken);

        if (!destination.IsValid)
        {
            return destination.Error;
        }

        return ValidateHeaders(headers);
    }

    private static string? ValidateHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        if (headers.Count > 10)
        {
            return "A webhook may include at most 10 custom headers.";
        }

        foreach (var (key, value) in headers)
        {
            if (
                !HeaderNamePattern.IsMatch(key)
                || ForbiddenHeaders.Contains(key)
                || key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase)
            )
            {
                return $"Webhook header '{key}' is not allowed.";
            }

            if (value.Length > 1024 || value.Contains('\r') || value.Contains('\n'))
            {
                return $"Webhook header '{key}' has an invalid value.";
            }
        }

        return null;
    }

    internal static Dictionary<string, string> NormalizeHeaders(Dictionary<string, string>? headers)
    {
        return headers?.ToDictionary(
                header => header.Key.Trim(),
                header => header.Value.Trim(),
                StringComparer.OrdinalIgnoreCase
            )
            ?? [];
    }

    internal static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsContractHash(string? value)
    {
        var hash = NormalizeHash(value);

        return hash.Length == 42
            && hash.StartsWith("0x", StringComparison.Ordinal)
            && hash[2..].All(Uri.IsHexDigit);
    }

    internal static string NormalizeHash(string? contractHash)
    {
        var normalized = contractHash?.Trim() ?? string.Empty;

        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? $"0x{normalized[2..].ToLowerInvariant()}"
            : $"0x{normalized.ToLowerInvariant()}";
    }
}
