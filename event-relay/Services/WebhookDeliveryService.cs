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
        var queued = await deliveries.EnqueueAsync(new WebhookDeliveryDocument
        {
            Id = id, SubscriptionId = subscription.Id, EventId = observedEvent.Id, WebhookUrl = subscription.WebhookUrl,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions), IdempotencyKey = key, EventName = observedEvent.EventName,
            Trigger = "automatic", Status = "pending", AttemptCount = 0, DeliveredAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow
        }, cancellationToken);
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
        if (string.IsNullOrWhiteSpace(subscription.ProjectId)
            || !await entitlements.TryConsumeEventAsync(subscription.ProjectId, subscription.Network, cancellationToken))
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

    public async Task DeliverAsync(
        WebhookSubscriptionDocument subscription,
        ObservedNeoEvent observedEvent,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = $"{subscription.Id}:{observedEvent.Id}";
        if (await deliveries.ExistsAsync(idempotencyKey, cancellationToken))
        {
            logger.LogDebug("Skipping duplicate webhook delivery {IdempotencyKey}.", idempotencyKey);
            return;
        }
        var deliveryId = Guid.NewGuid().ToString("n");
        var payload = new WebhookPayload(
            deliveryId,
            subscription.Id,
            observedEvent.Network,
            observedEvent.BlockIndex,
            observedEvent.TransactionHash,
            observedEvent.ContractHash,
            observedEvent.EventName,
            observedEvent.State,
            observedEvent.ObservedAt);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await DeliverWithRetriesAsync(subscription, deliveryId, observedEvent.Id, json, observedEvent.Network,
            observedEvent.ContractHash, observedEvent.EventName, idempotencyKey, cancellationToken);
    }

    public async Task DeliverTestAsync(WebhookSubscriptionDocument subscription, CancellationToken cancellationToken)
    {
        var deliveryId = Guid.NewGuid().ToString("n");
        var payload = new WebhookPayload(deliveryId, subscription.Id, subscription.Network, 0,
            "test-event", subscription.ContractHash, subscription.EventName ?? "Pusharoo.Test",
            JsonSerializer.SerializeToElement(new { test = true, message = "Pusharoo test delivery" }), DateTime.UtcNow);
        await DeliverWithRetriesAsync(subscription, deliveryId, $"test-{deliveryId}", JsonSerializer.Serialize(payload, JsonOptions),
            subscription.Network, subscription.ContractHash, subscription.EventName ?? "Pusharoo.Test", $"test:{deliveryId}", cancellationToken);
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

    private async Task DeliverWithRetriesAsync(WebhookSubscriptionDocument subscription, string deliveryId, string eventId,
        string json, string network, string contractHash, string eventName, string idempotencyKey, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, _options.WebhookMaxAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (await DeliverPayloadAsync(subscription, deliveryId, eventId, json, network, contractHash, eventName,
                idempotencyKey, attempt, attempt == attempts, cancellationToken)) return;
            if (attempt < attempts)
            {
                var delay = Math.Pow(2, attempt - 1) * Math.Max(1, _options.WebhookRetryBaseSeconds);
                await Task.Delay(TimeSpan.FromSeconds(delay + Random.Shared.NextDouble()), cancellationToken);
            }
        }
    }

    private async Task<bool> DeliverPayloadAsync(WebhookSubscriptionDocument subscription, string deliveryId, string eventId,
        string json, string network, string contractHash, string eventName, string idempotencyKey, int attempt, bool finalAttempt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.WebhookTimeoutSeconds)));

        int? statusCode = null;
        string? error = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.WebhookUrl);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Delivery", deliveryId);
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Event", eventName);

            var secret = secretProtector.Unprotect(subscription.Secret);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                request.Headers.TryAddWithoutValidation(
                    "X-Pusharoo-Signature",
                    WebhookSignature.Create(secret, json));
            }

            foreach (var (key, value) in subscription.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(key, value))
                {
                    request.Content.Headers.TryAddWithoutValidation(key, value);
                }
            }

            using var response = await httpClient.SendAsync(request, timeout.Token);
            statusCode = (int)response.StatusCode;

            var succeeded = statusCode.Value is >= 200 and <= 299;
            if (succeeded || finalAttempt) await RecordAsync(succeeded, null, statusCode, cancellationToken);
            return succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = ex.Message;
            logger.LogWarning(
                ex,
                "Webhook delivery {DeliveryId} failed for subscription {SubscriptionId}.",
                deliveryId,
                subscription.Id);

            if (finalAttempt) await RecordAsync(false, error, statusCode, cancellationToken);
            return false;
        }

        async Task RecordAsync(bool succeeded, string? failure, int? code, CancellationToken recordCancellationToken)
        {
            await deliveries.InsertAsync(
                new WebhookDeliveryDocument
                {
                    Id = deliveryId,
                    SubscriptionId = subscription.Id,
                    EventId = eventId,
                    WebhookUrl = subscription.WebhookUrl,
                    StatusCode = code,
                    Succeeded = succeeded,
                    Error = failure,
                    DeliveredAt = DateTime.UtcNow,
                    PayloadJson = json
                    , IdempotencyKey = idempotencyKey
                    , AttemptCount = attempt
                    , Status = succeeded ? "succeeded" : "dead_letter"
                },
                recordCancellationToken);

            if (!succeeded && code is not null)
            {
                logger.LogWarning(
                    "Webhook delivery {DeliveryId} returned HTTP {StatusCode}.",
                    deliveryId,
                    (HttpStatusCode)code.Value);
            }
        }
    }

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
