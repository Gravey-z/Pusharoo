using System.Text;
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
    ILogger<WebhookDeliveryService> logger) : IEventDeliveryQueue
{
    private readonly EventRelayOptions _options = options.Value;

    public async Task QueueAsync(WebhookSubscriptionDocument subscription, ObservedNeoEvent observedEvent, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("n");
        var key = $"{subscription.Id}:{observedEvent.Id}";
        var payload = WebhookPayloadContract.Create(id, subscription, observedEvent);
        var queued = await deliveries.EnqueueAsync(CreateQueuedDelivery(
            id,
            subscription,
            observedEvent.Id,
            payload,
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
        if (subscription is null)
        {
            await CompleteAsync(delivery, false, false, null, "Webhook is no longer active.", 0, cancellationToken);
            return true;
        }
        if (!subscription.IsEnabled)
        {
            await deliveries.DeferAsync(delivery, "Subscription is paused; waiting for resume.", cancellationToken);
            return true;
        }
        if (delivery.Trigger != "test" && !delivery.AllowanceCharged)
        {
            if (delivery.AllowancePeriodId is not null &&
                await entitlements.WasChargedAsync(delivery.AllowancePeriodId, delivery.Id, cancellationToken))
                delivery = delivery with { AllowanceCharged = true };
            else
            {
                if (string.IsNullOrWhiteSpace(subscription.ProjectId))
                {
                    await CompleteAsync(delivery, false, false, null, "Subscription project is missing.", 0, cancellationToken);
                    return true;
                }
                var entitlement = await entitlements.GetAsync(subscription.ProjectId, subscription.Network, cancellationToken);
                // A new period is published only after closing the previous ledger. Recheck its final receipt first.
                if (delivery.AllowancePeriodId is not null && delivery.AllowancePeriodId != RelayEntitlementService.PeriodId(entitlement)
                    && await entitlements.WasChargedAsync(delivery.AllowancePeriodId, delivery.Id, cancellationToken))
                {
                    delivery = delivery with { AllowanceCharged = true };
                    if (!await deliveries.SaveAllowanceAsync(delivery, cancellationToken)) return true;
                }
                else
                {
                    delivery = delivery with { AllowancePeriodId = RelayEntitlementService.PeriodId(entitlement) };
                    // Persist the selected period before charging: a crash can recover the receipt even after renewal.
                    if (!await deliveries.SaveAllowanceAsync(delivery, cancellationToken)) return true;
                    if (!await entitlements.TryConsumeEventAsync(entitlement, delivery.Id, cancellationToken))
                    {
                        await deliveries.DeferAsync(delivery, "Allowance exhausted or inactive; waiting for renewal.", cancellationToken);
                        return true;
                    }
                    delivery = delivery with { AllowanceCharged = true };
                }
            }
            if (!await deliveries.SaveAllowanceAsync(delivery, cancellationToken)) return true;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        int? statusCode = null;
        string? error = null;
        var succeeded = false;
        var retryable = true;
        try
        {
            delivery.PayloadJson = WebhookPayloadContract.ForDelivery(delivery.PayloadJson ?? throw new FormatException("Stored webhook payload is missing."),delivery.Id,delivery.EventId);
            var payloadVersion = WebhookPayloadContract.Version(delivery.PayloadJson);
            if (payloadVersion != delivery.PayloadVersion) throw new FormatException("Stored payload version does not match the delivery.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.WebhookTimeoutSeconds)));
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.WebhookUrl) { Content = new StringContent(delivery.PayloadJson ?? "{}", Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Delivery", delivery.Id);
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Event", delivery.EventName);
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Event-Id", delivery.EventId);
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Payload-Version", delivery.PayloadVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var secret = secretProtector.Unprotect(subscription.Secret);
            if (!string.IsNullOrWhiteSpace(secret)) request.Headers.TryAddWithoutValidation("X-Pusharoo-Signature", WebhookSignature.Create(secret, delivery.PayloadJson ?? "{}"));
            foreach (var (key, value) in subscription.Headers) if (!WebhookPayloadContract.IsTransportHeader(key) && !request.Headers.TryAddWithoutValidation(key, value)) request.Content.Headers.TryAddWithoutValidation(key, value);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            statusCode = (int)response.StatusCode;
            succeeded = statusCode is >= 200 and <= 299;
            retryable = !succeeded && (statusCode is 408 or 425 or 429 || statusCode >= 500);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = ex.Message;
            retryable = ex is not FormatException;
        }
        // Database failures here propagate to the worker; they are never reclassified as HTTP failures.
        await CompleteAsync(delivery, succeeded, retryable, statusCode, error, started.ElapsedMilliseconds, cancellationToken);
        return true;
    }

    private async Task CompleteAsync(WebhookDeliveryDocument delivery, bool succeeded, bool retryable, int? statusCode, string? error, long latency, CancellationToken cancellationToken)
    {
        var attempt = delivery.AttemptCount + 1;
        var dead = !succeeded && (!retryable || attempt >= Math.Max(1, _options.WebhookMaxAttempts));
        DateTime? next = !succeeded && !dead ? DateTime.UtcNow.AddSeconds(Math.Min(3600, Math.Pow(2, Math.Min(16, attempt - 1)) * Math.Max(1, _options.WebhookRetryBaseSeconds)) + Random.Shared.NextDouble()) : null;
        var updated = delivery with { DeliveredAt = DateTime.UtcNow, StatusCode = statusCode, Succeeded = succeeded,
            Error = error, AttemptCount = attempt, LatencyMilliseconds = latency,
            Status = succeeded ? "succeeded" : dead ? "dead_letter" : "retrying", NextAttemptAt = next };
        var record = new WebhookDeliveryAttemptDocument { Id = Guid.NewGuid().ToString("n"), DeliveryId = delivery.Id, EventId = delivery.EventId, PayloadVersion = delivery.PayloadVersion, AttemptNumber = attempt, StatusCode = statusCode, Succeeded = succeeded, Retryable = retryable, Error = error, LatencyMilliseconds = latency, AttemptedAt = DateTime.UtcNow };
        if (await deliveries.CompleteAsync(updated, record, cancellationToken)) operations.RecordDelivery(succeeded, !succeeded && !dead, dead, latency);
    }

    public async Task<WebhookDeliveryDocument> QueueTestAsync(WebhookSubscriptionDocument subscription, CancellationToken cancellationToken)
    {
        var deliveryId = Guid.NewGuid().ToString("n");
        var eventId = $"test-{deliveryId}";
        var payload = WebhookPayloadContract.CreateTest(deliveryId, eventId, subscription);
        var delivery = CreateQueuedDelivery(
            deliveryId,
            subscription,
            eventId,
            payload,
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
            throw new InvalidOperationException("The stored payload has expired. Block replay only recovers missing events; retained identities skip this delivery.");
        }

        var deliveryId = Guid.NewGuid().ToString("n");
        var payload = WebhookPayloadContract.ForDelivery(original.PayloadJson, deliveryId, original.EventId, manual: true);
        await deliveries.EnqueueAsync(new WebhookDeliveryDocument { Id = deliveryId, SubscriptionId = subscription.Id, EventId = original.EventId, WebhookUrl = subscription.WebhookUrl, PayloadJson = payload, PayloadVersion = WebhookPayloadContract.Version(payload), IdempotencyKey = $"manual:{original.Id}:{deliveryId}", EventName = original.EventName, Trigger = "manual", RedeliveryOfDeliveryId = original.Id, Status = "pending", AttemptCount = 0, DeliveredAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow }, cancellationToken);
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
            PayloadVersion = subscription.PayloadVersion,
            IdempotencyKey = idempotencyKey,
            EventName = eventName,
            Trigger = trigger,
            Status = "pending",
            AttemptCount = 0,
            DeliveredAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow
        };

}
