using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookDeliveryService(
    HttpClient httpClient,
    IWebhookDeliveryRepository deliveries,
    RelayEntitlementService entitlements,
    RelayOperationsService operations,
    WebhookSecretProtector secretProtector,
    IOptions<EventRelayOptions> options,
    ILogger<WebhookDeliveryService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EventRelayOptions _options = options.Value;

    public async Task QueueAsync(WebhookSubscriptionDocument subscription, ObservedNeoEvent observedEvent, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("n");
        var key = $"{subscription.Id}:{observedEvent.Id}";
        var payload = new WebhookPayload(id, subscription.Id, observedEvent.Network, observedEvent.BlockIndex,
            observedEvent.TransactionHash, observedEvent.ContractHash, observedEvent.EventName, observedEvent.State, observedEvent.ObservedAt);
        var queued = await deliveries.EnqueueAsync(CreateQueuedDelivery(
            id,
            subscription,
            observedEvent.Id,
            JsonSerializer.Serialize(payload, JsonOptions),
            key,
            observedEvent.EventName,
            "automatic"), cancellationToken);
        if (!queued)
        {
            logger.LogDebug("Skipping duplicate webhook delivery {IdempotencyKey}.", key);
        }
    }

    public async Task<bool> ProcessNextAsync(IWebhookSubscriptionRepository subscriptions, CancellationToken cancellationToken)
    {
        var delivery = await deliveries.ClaimDueAsync(cancellationToken);
        if (delivery is null) return false;
        var subscription = await subscriptions.GetByIdAsync(delivery.SubscriptionId, cancellationToken);
        if (subscription is null || !subscription.IsEnabled)
        {
            await CompleteAsync(delivery, false, false, null, "Webhook is no longer active.", 0, cancellationToken);
            return true;
        }
        if (delivery.Trigger != "test" && (string.IsNullOrWhiteSpace(subscription.ProjectId)
            || !await entitlements.TryConsumeEventAsync(subscription.ProjectId, subscription.Network, cancellationToken))
        )
        {
            await CompleteAsync(delivery, false, false, null, "Relay event allowance is exhausted or inactive.", 0, cancellationToken);
            return true;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        int? statusCode = null;
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.WebhookTimeoutSeconds)));
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.WebhookUrl) { Content = new StringContent(delivery.PayloadJson ?? "{}", Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Delivery", delivery.Id);
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Event", delivery.EventName);
            var secret = secretProtector.Unprotect(subscription.Secret);
            if (!string.IsNullOrWhiteSpace(secret)) request.Headers.TryAddWithoutValidation("X-Pusharoo-Signature", WebhookSignature.Create(secret, delivery.PayloadJson ?? "{}"));
            foreach (var (key, value) in subscription.Headers) if (!request.Headers.TryAddWithoutValidation(key, value)) request.Content.Headers.TryAddWithoutValidation(key, value);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            statusCode = (int)response.StatusCode;
            var succeeded = statusCode is >= 200 and <= 299;
            var retryable = !succeeded && (statusCode is 408 or 425 or 429 || statusCode >= 500);
            await CompleteAsync(delivery, succeeded, retryable, statusCode, null, started.ElapsedMilliseconds, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = ex.Message;
            await CompleteAsync(delivery, false, true, statusCode, error, started.ElapsedMilliseconds, cancellationToken);
        }
        return true;
    }

    private async Task CompleteAsync(WebhookDeliveryDocument delivery, bool succeeded, bool retryable, int? statusCode, string? error, long latency, CancellationToken cancellationToken)
    {
        var attempt = delivery.AttemptCount + 1;
        var dead = !succeeded && (!retryable || attempt >= Math.Max(1, _options.WebhookMaxAttempts));
        DateTime? next = !succeeded && !dead ? DateTime.UtcNow.AddSeconds(Math.Pow(2, attempt - 1) * Math.Max(1, _options.WebhookRetryBaseSeconds) + Random.Shared.NextDouble()) : null;
        var updated = new WebhookDeliveryDocument { Id = delivery.Id, SubscriptionId = delivery.SubscriptionId, EventId = delivery.EventId, WebhookUrl = delivery.WebhookUrl, PayloadJson = delivery.PayloadJson, IdempotencyKey = delivery.IdempotencyKey, EventName = delivery.EventName, Trigger = delivery.Trigger, RedeliveryOfDeliveryId = delivery.RedeliveryOfDeliveryId, DeliveredAt = DateTime.UtcNow, StatusCode = statusCode, Succeeded = succeeded, Error = error, AttemptCount = attempt, LatencyMilliseconds = latency, Status = succeeded ? "succeeded" : dead ? "dead_letter" : "retrying", NextAttemptAt = next };
        var record = new WebhookDeliveryAttemptDocument { Id = Guid.NewGuid().ToString("n"), DeliveryId = delivery.Id, AttemptNumber = attempt, StatusCode = statusCode, Succeeded = succeeded, Retryable = retryable, Error = error, LatencyMilliseconds = latency, AttemptedAt = DateTime.UtcNow };
        await deliveries.CompleteAsync(updated, record, cancellationToken);
        operations.RecordDelivery(succeeded, !succeeded && !dead, dead, latency);
    }

    public async Task<WebhookDeliveryDocument> QueueTestAsync(WebhookSubscriptionDocument subscription, CancellationToken cancellationToken)
    {
        var deliveryId = Guid.NewGuid().ToString("n");
        var payload = new WebhookPayload(deliveryId, subscription.Id, subscription.Network, 0,
            "test-event", subscription.ContractHash, subscription.EventName ?? "Pusharoo.Test",
            JsonSerializer.SerializeToElement(new { test = true, message = "Pusharoo test delivery" }), DateTime.UtcNow);
        var delivery = CreateQueuedDelivery(
            deliveryId,
            subscription,
            $"test-{deliveryId}",
            JsonSerializer.Serialize(payload, JsonOptions),
            $"test:{deliveryId}",
            subscription.EventName ?? "Pusharoo.Test",
            "test");
        await deliveries.EnqueueAsync(delivery, cancellationToken);
        return delivery;
    }

    public async Task RedeliverAsync(WebhookSubscriptionDocument subscription, WebhookDeliveryDocument original, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(original.PayloadJson))
        {
            throw new InvalidOperationException("This delivery was recorded before payload replay was available.");
        }

        var deliveryId = Guid.NewGuid().ToString("n");
        await deliveries.EnqueueAsync(new WebhookDeliveryDocument { Id = deliveryId, SubscriptionId = subscription.Id, EventId = original.EventId, WebhookUrl = subscription.WebhookUrl, PayloadJson = original.PayloadJson, IdempotencyKey = $"manual:{original.Id}:{deliveryId}", EventName = original.EventName, Trigger = "manual", RedeliveryOfDeliveryId = original.Id, Status = "pending", AttemptCount = 0, DeliveredAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow }, cancellationToken);
    }

    private static WebhookDeliveryDocument CreateQueuedDelivery(
        string id,
        WebhookSubscriptionDocument subscription,
        string eventId,
        string payloadJson,
        string idempotencyKey,
        string eventName,
        string trigger) => new()
        {
            Id = id,
            SubscriptionId = subscription.Id,
            EventId = eventId,
            WebhookUrl = subscription.WebhookUrl,
            PayloadJson = payloadJson,
            IdempotencyKey = idempotencyKey,
            EventName = eventName,
            Trigger = trigger,
            Status = "pending",
            AttemptCount = 0,
            DeliveredAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow
        };

    private sealed record WebhookPayload(
        string DeliveryId,
        string SubscriptionId,
        string Network,
        uint BlockIndex,
        string TransactionHash,
        string ContractHash,
        string EventName,
        JsonElement State,
        DateTime ObservedAt);
}
