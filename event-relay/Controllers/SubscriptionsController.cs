using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Controllers;

[ApiController]
[EnableRateLimiting("WebhookManagement")]
[Route("api/projects/{projectId}/subscriptions")]
public sealed class SubscriptionsController(
    IWebhookSubscriptionRepository subscriptions,
    IWebhookDeliveryRepository deliveries,
    ProjectAccessClient projectAccess,
    WebhookDestinationValidator destinationValidator,
    WebhookSecretProtector secretProtector,
    IOptions<NeoRpcOptions> neoRpcOptions) : ControllerBase
{
    private static readonly Regex HeaderNamePattern = new("^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,64}$", RegexOptions.Compiled);
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
        "X-Pusharoo-Signature"
    };

    [HttpPost("query")]
    public async Task<ActionResult<IReadOnlyList<SubscriptionResponse>>> GetAll(
        string projectId,
        WebhookAccessRequest request,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(
            projectId,
            "subscriptions.read",
            WebhookManagementRequestHasher.Hash(projectId, "subscriptions.read"),
            request.Signature,
            cancellationToken);
        if (authorization is not null)
        {
            return authorization;
        }

        var items = await subscriptions.GetByProjectIdAsync(projectId, cancellationToken);
        var response = await Task.WhenAll(items.Select(async subscription => ToResponse(
            subscription,
            await deliveries.GetLatestBySubscriptionAsync(subscription.Id, cancellationToken))));

        return Ok(response);
    }

    [HttpPost]
    public async Task<ActionResult<SubscriptionResponse>> Create(
        string projectId,
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(
            projectId,
            "subscriptions.create",
            WebhookManagementRequestHasher.HashCreate(projectId, request),
            request.Signature,
            cancellationToken);
        if (authorization is not null)
        {
            return authorization;
        }

        var validation = await ValidateSubscriptionAsync(request, cancellationToken);
        if (validation is not null)
        {
            return BadRequest(new { error = validation });
        }

        var now = DateTime.UtcNow;
        var subscription = new WebhookSubscriptionDocument
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = projectId.Trim(),
            Name = request.Name.Trim(),
            ContractHash = NormalizeHash(request.ContractHash),
            Network = request.Network.Trim(),
            EventName = NormalizeOptional(request.EventName),
            WebhookUrl = request.WebhookUrl.Trim(),
            Secret = secretProtector.Protect(request.Secret),
            Headers = NormalizeHeaders(request.Headers),
            IsEnabled = request.IsEnabled,
            CreatedAt = now,
            UpdatedAt = now
        };

        await subscriptions.InsertAsync(subscription, cancellationToken);

        return CreatedAtAction(nameof(GetAll), new { projectId }, ToResponse(subscription, null));
    }

    [HttpPut("{subscriptionId}")]
    public async Task<ActionResult<SubscriptionResponse>> Update(
        string projectId,
        string subscriptionId,
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await GetProjectSubscriptionAsync(projectId, subscriptionId, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        var authorization = await AuthorizeAsync(
            projectId,
            "subscriptions.update",
            WebhookManagementRequestHasher.HashUpdate(projectId, subscriptionId, request),
            request.Signature,
            cancellationToken);
        if (authorization is not null)
        {
            return authorization;
        }

        var validation = await ValidateSubscriptionAsync(request, cancellationToken);
        if (validation is not null)
        {
            return BadRequest(new { error = validation });
        }

        var updated = new WebhookSubscriptionDocument
        {
            Id = existing.Id,
            ProjectId = existing.ProjectId,
            Name = request.Name.Trim(),
            ContractHash = NormalizeHash(request.ContractHash),
            Network = request.Network.Trim(),
            EventName = NormalizeOptional(request.EventName),
            WebhookUrl = request.WebhookUrl.Trim(),
            Secret = string.IsNullOrWhiteSpace(request.Secret)
                ? existing.Secret
                : secretProtector.Protect(request.Secret),
            Headers = NormalizeHeaders(request.Headers),
            IsEnabled = request.IsEnabled,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        await subscriptions.ReplaceAsync(updated, cancellationToken);

        return Ok(ToResponse(updated, await deliveries.GetLatestBySubscriptionAsync(updated.Id, cancellationToken)));
    }

    [HttpDelete("{subscriptionId}")]
    public async Task<IActionResult> Delete(
        string projectId,
        string subscriptionId,
        WebhookAccessRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await GetProjectSubscriptionAsync(projectId, subscriptionId, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        var authorization = await AuthorizeAsync(
            projectId,
            "subscriptions.delete",
            WebhookManagementRequestHasher.Hash(projectId, "subscriptions.delete", subscriptionId),
            request.Signature,
            cancellationToken);
        if (authorization is not null)
        {
            return authorization;
        }

        await subscriptions.DeleteAsync(subscriptionId, cancellationToken);
        return NoContent();
    }

    [HttpPost("{subscriptionId}/deliveries/query")]
    public async Task<ActionResult<IReadOnlyList<WebhookDeliveryDocument>>> GetDeliveries(
        string projectId,
        string subscriptionId,
        WebhookAccessRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await GetProjectSubscriptionAsync(projectId, subscriptionId, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        var authorization = await AuthorizeAsync(
            projectId,
            "deliveries.read",
            WebhookManagementRequestHasher.Hash(projectId, "deliveries.read", subscriptionId),
            request.Signature,
            cancellationToken);
        if (authorization is not null)
        {
            return authorization;
        }

        return Ok(await deliveries.GetBySubscriptionAsync(subscriptionId, cancellationToken));
    }

    private async Task<ActionResult?> AuthorizeAsync(
        string projectId,
        string operation,
        string requestHash,
        WalletSignatureRequest? signature,
        CancellationToken cancellationToken)
    {
        var result = await projectAccess.ValidateAsync(
            projectId,
            operation,
            requestHash,
            signature,
            cancellationToken);

        return result.IsAllowed ? null : StatusCode(result.StatusCode, new { error = result.Error });
    }

    private async Task<WebhookSubscriptionDocument?> GetProjectSubscriptionAsync(
        string projectId,
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(subscriptionId, cancellationToken);
        return subscription is not null
            && string.Equals(subscription.ProjectId, projectId, StringComparison.Ordinal)
            ? subscription
            : null;
    }

    private async Task<string?> ValidateSubscriptionAsync(
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        return await ValidateSubscriptionAsync(
            request.Name,
            request.ContractHash,
            request.Network,
            request.WebhookUrl,
            request.Headers,
            cancellationToken);
    }

    private async Task<string?> ValidateSubscriptionAsync(
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        return await ValidateSubscriptionAsync(
            request.Name,
            request.ContractHash,
            request.Network,
            request.WebhookUrl,
            request.Headers,
            cancellationToken);
    }

    private async Task<string?> ValidateSubscriptionAsync(
        string name,
        string contractHash,
        string network,
        string webhookUrl,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
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
            if (!HeaderNamePattern.IsMatch(key)
                || ForbiddenHeaders.Contains(key)
                || key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase))
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

    private static Dictionary<string, string> NormalizeHeaders(Dictionary<string, string>? headers)
    {
        return headers?.ToDictionary(
            header => header.Key.Trim(),
            header => header.Value.Trim(),
            StringComparer.OrdinalIgnoreCase) ?? [];
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsContractHash(string? value)
    {
        var hash = NormalizeHash(value);
        return hash.Length == 42 && hash.StartsWith("0x", StringComparison.Ordinal) && hash[2..].All(Uri.IsHexDigit);
    }

    private static string NormalizeHash(string? contractHash)
    {
        var normalized = contractHash?.Trim() ?? string.Empty;
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? $"0x{normalized[2..].ToLowerInvariant()}"
            : $"0x{normalized.ToLowerInvariant()}";
    }

    private static SubscriptionResponse ToResponse(
        WebhookSubscriptionDocument subscription,
        WebhookDeliveryDocument? latestDelivery)
    {
        return new SubscriptionResponse(
            subscription.Id,
            subscription.ProjectId ?? string.Empty,
            subscription.Name,
            subscription.ContractHash,
            subscription.Network,
            subscription.EventName,
            subscription.WebhookUrl,
            subscription.Headers,
            subscription.IsEnabled,
            subscription.CreatedAt,
            subscription.UpdatedAt,
            latestDelivery);
    }
}
