using Microsoft.AspNetCore.Mvc;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Controllers;

[ApiController]
[Route("api/subscriptions")]
public sealed class SubscriptionsController(
    IWebhookSubscriptionRepository subscriptions,
    IWebhookDeliveryRepository deliveries) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SubscriptionResponse>> Create(
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.WebhookUrl, UriKind.Absolute, out var webhookUri)
            || (webhookUri.Scheme != Uri.UriSchemeHttp && webhookUri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("WebhookUrl must be an absolute HTTP or HTTPS URL.");
        }

        var now = DateTime.UtcNow;
        var subscription = new WebhookSubscriptionDocument
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = string.IsNullOrWhiteSpace(request.ProjectId) ? null : request.ProjectId.Trim(),
            Name = request.Name.Trim(),
            ContractHash = NormalizeHash(request.ContractHash),
            EventName = string.IsNullOrWhiteSpace(request.EventName) ? null : request.EventName.Trim(),
            WebhookUrl = request.WebhookUrl.Trim(),
            Secret = string.IsNullOrWhiteSpace(request.Secret) ? null : request.Secret,
            Headers = request.Headers ?? [],
            IsEnabled = request.IsEnabled,
            CreatedAt = now,
            UpdatedAt = now
        };

        await subscriptions.InsertAsync(subscription, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { subscriptionId = subscription.Id }, ToResponse(subscription));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SubscriptionResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await subscriptions.GetAllAsync(cancellationToken);
        return Ok(items.Select(ToResponse).ToList());
    }

    [HttpGet("{subscriptionId}")]
    public async Task<ActionResult<SubscriptionResponse>> GetById(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(subscriptionId, cancellationToken);
        return subscription is null ? NotFound() : Ok(ToResponse(subscription));
    }

    [HttpPut("{subscriptionId}")]
    public async Task<ActionResult<SubscriptionResponse>> Update(
        string subscriptionId,
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await subscriptions.GetByIdAsync(subscriptionId, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        if (!Uri.TryCreate(request.WebhookUrl, UriKind.Absolute, out var webhookUri)
            || (webhookUri.Scheme != Uri.UriSchemeHttp && webhookUri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("WebhookUrl must be an absolute HTTP or HTTPS URL.");
        }

        var updated = new WebhookSubscriptionDocument
        {
            Id = existing.Id,
            ProjectId = string.IsNullOrWhiteSpace(request.ProjectId) ? null : request.ProjectId.Trim(),
            Name = request.Name.Trim(),
            ContractHash = NormalizeHash(request.ContractHash),
            EventName = string.IsNullOrWhiteSpace(request.EventName) ? null : request.EventName.Trim(),
            WebhookUrl = request.WebhookUrl.Trim(),
            Secret = string.IsNullOrWhiteSpace(request.Secret) ? existing.Secret : request.Secret,
            Headers = request.Headers ?? [],
            IsEnabled = request.IsEnabled,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        await subscriptions.ReplaceAsync(updated, cancellationToken);

        return Ok(ToResponse(updated));
    }

    [HttpDelete("{subscriptionId}")]
    public async Task<IActionResult> Delete(string subscriptionId, CancellationToken cancellationToken)
    {
        var deleted = await subscriptions.DeleteAsync(subscriptionId, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [HttpGet("{subscriptionId}/deliveries")]
    public async Task<ActionResult<IReadOnlyList<WebhookDeliveryDocument>>> GetDeliveries(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(subscriptionId, cancellationToken);
        if (subscription is null)
        {
            return NotFound();
        }

        return Ok(await deliveries.GetBySubscriptionAsync(subscriptionId, cancellationToken));
    }

    private static SubscriptionResponse ToResponse(WebhookSubscriptionDocument subscription)
    {
        return new SubscriptionResponse(
            subscription.Id,
            subscription.ProjectId,
            subscription.Name,
            subscription.ContractHash,
            subscription.EventName,
            subscription.WebhookUrl,
            subscription.Headers,
            subscription.IsEnabled,
            subscription.CreatedAt,
            subscription.UpdatedAt);
    }

    private static string NormalizeHash(string contractHash)
    {
        return contractHash.Trim().ToLowerInvariant();
    }
}

public sealed record CreateSubscriptionRequest(
    string Name,
    string ContractHash,
    string? EventName,
    string WebhookUrl,
    string? ProjectId,
    string? Secret,
    Dictionary<string, string>? Headers,
    bool IsEnabled = true);

public sealed record UpdateSubscriptionRequest(
    string Name,
    string ContractHash,
    string? EventName,
    string WebhookUrl,
    string? ProjectId,
    string? Secret,
    Dictionary<string, string>? Headers,
    bool IsEnabled = true);

public sealed record SubscriptionResponse(
    string Id,
    string? ProjectId,
    string Name,
    string ContractHash,
    string? EventName,
    string WebhookUrl,
    Dictionary<string, string> Headers,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);
