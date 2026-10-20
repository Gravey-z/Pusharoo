using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookDeliveryService(
    IWebhookTransport transport,
    IWebhookDeliveryRepository deliveries,
    RelayEntitlementService entitlements,
    RelayOperationsService operations,
    IOptions<EventRelayOptions> options,
    ILogger<WebhookDeliveryService> logger,
    TimeProvider? timeProvider = null
) : IEventDeliveryQueue
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private readonly EventRelayOptions _options = options.Value;

    public async Task QueueAsync(
        WebhookSubscriptionDocument subscription,
        ObservedNeoEvent observedEvent,
        CancellationToken cancellationToken
    )
    {
        var id = Guid.NewGuid().ToString("n");
        var key = $"{subscription.Id}:{observedEvent.Id}";
        var payload = WebhookPayloadContract.Create(id, subscription, observedEvent);
        var queued = await deliveries.EnqueueAsync(
            CreateQueuedDelivery(
                id,
                subscription,
                observedEvent.Id,
                payload,
                key,
                observedEvent.EventName,
                "automatic"
            ),
            cancellationToken
        );

        if (!queued)
        {
            logger.LogDebug("Skipping duplicate webhook delivery {IdempotencyKey}.", key);
        }
    }

    public async Task<bool> ProcessNextAsync(
        IWebhookSubscriptionRepository subscriptions,
        CancellationToken cancellationToken
    )
    {
        var delivery = await deliveries.ClaimDueAsync(cancellationToken);

        if (delivery is null)
            return false;

        var subscription = await subscriptions.GetByIdAsync(
            delivery.SubscriptionId,
            cancellationToken
        );

        if (subscription is null)
        {
            await CompleteAsync(
                delivery,
                false,
                false,
                null,
                "Webhook is no longer active.",
                0,
                cancellationToken
            );

            return true;
        }

        if (!string.IsNullOrEmpty(delivery.Network) && delivery.Network != subscription.Network)
        {
            await CompleteAsync(
                delivery,
                false,
                false,
                null,
                "Subscription network changed after this event was queued.",
                0,
                cancellationToken
            );

            return true;
        }

        delivery = delivery with { Network = subscription.Network };

        if (entitlements is not null && subscription.ProjectId is not null)
        {
            subscription = await entitlements.Lifecycle.EffectiveSubscriptionAsync(subscription, cancellationToken);
            if (subscription is null)
            {
                await deliveries.DeferAsync(delivery, "Webhook changed; waiting for reconciliation.", cancellationToken);
                return true;
            }
        }

        if (!subscription.IsEnabled)
        {
            await deliveries.DeferAsync(
                delivery,
                "Subscription is paused; waiting for resume.",
                cancellationToken
            );

            return true;
        }

        if (delivery.Network != subscription.Network)
        {
            await CompleteAsync(delivery, false, false, null, "Subscription network changed after this event was queued.", 0, cancellationToken);
            return true;
        }

        if (delivery.Trigger != "test" && !delivery.AllowanceCharged)
        {
            if (
                delivery.AllowancePeriodId is not null
                && await entitlements!.WasChargedAsync(
                    delivery.AllowancePeriodId,
                    delivery.Id,
                    cancellationToken
                )
            )
                delivery = delivery with { AllowanceCharged = true };
            else
            {
                if (string.IsNullOrWhiteSpace(subscription.ProjectId))
                {
                    await CompleteAsync(
                        delivery,
                        false,
                        false,
                        null,
                        "Subscription project is missing.",
                        0,
                        cancellationToken
                    );

                    return true;
                }

                var entitlement = await entitlements!.GetAsync(
                    subscription.ProjectId,
                    subscription.Network,
                    cancellationToken
                );
                // A new period is published only after closing the previous ledger. Recheck its final receipt first.

                if (
                    delivery.AllowancePeriodId is not null
                    && delivery.AllowancePeriodId != RelayEntitlementService.PeriodId(entitlement)
                    && await entitlements.WasChargedAsync(
                        delivery.AllowancePeriodId,
                        delivery.Id,
                        cancellationToken
                    )
                )
                {
                    delivery = delivery with { AllowanceCharged = true };

                    if (!await deliveries.SaveAllowanceAsync(delivery, cancellationToken))
                        return true;
                }
                else
                {
                    delivery = delivery with
                    {
                        AllowancePeriodId = RelayEntitlementService.PeriodId(entitlement)
                    };
                    // Persist the selected period before charging: a crash can recover the receipt even after renewal.

                    if (!await deliveries.SaveAllowanceAsync(delivery, cancellationToken))
                        return true;

                    if (
                        !await entitlements.TryConsumeEventAsync(
                            entitlement,
                            delivery.Id,
                            cancellationToken,
                            subscription.Id
                        )
                    )
                    {
                        await deliveries.DeferAsync(
                            delivery,
                            "Allowance exhausted or inactive; waiting for renewal.",
                            cancellationToken
                        );

                        return true;
                    }

                    delivery = delivery with { AllowanceCharged = true };
                }
            }

            if (!await deliveries.SaveAllowanceAsync(delivery, cancellationToken))
                return true;
        }

        if (entitlements is not null && subscription.ProjectId is { } projectId
            && !await entitlements.Lifecycle.AdmitAttemptAsync(projectId, subscription.Id, subscription.Network, cancellationToken))
        {
            await deliveries.DeferAsync(delivery, "Subscription admission changed; waiting for resume.", cancellationToken);
            return true;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await DeliverAsync(subscription, delivery, cancellationToken);

        // Database failures here propagate to the worker; they are never reclassified as HTTP failures.

        await CompleteAsync(
            delivery,
            outcome.Succeeded,
            outcome.Retryable,
            outcome.StatusCode,
            outcome.Error,
            started.ElapsedMilliseconds,
            cancellationToken
        );

        return true;
    }

    private async Task<WebhookTransportResult> DeliverAsync(
        WebhookSubscriptionDocument subscription,
        WebhookDeliveryDocument delivery,
        CancellationToken cancellationToken
    )
    {
        try
        {
            delivery.PayloadJson = WebhookPayloadContract.ForDelivery(
                delivery.PayloadJson
                    ?? throw new FormatException("Stored webhook payload is missing."),
                delivery.Id,
                delivery.EventId
            );
            var payloadVersion = WebhookPayloadContract.Version(delivery.PayloadJson);

            if (payloadVersion != delivery.PayloadVersion)
                throw new FormatException("Stored payload version does not match the delivery.");
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested
            )
        {
            return new WebhookTransportResult(false, ex is not FormatException, null, ex.Message);
        }

        return await transport.SendAsync(subscription, delivery, cancellationToken);
    }

    private async Task CompleteAsync(
        WebhookDeliveryDocument delivery,
        bool succeeded,
        bool retryable,
        int? statusCode,
        string? error,
        long latency,
        CancellationToken cancellationToken
    )
    {
        var attempt = delivery.AttemptCount + 1;
        var dead =
            !succeeded && (!retryable || attempt >= Math.Max(1, _options.WebhookMaxAttempts));
        DateTime? next =
            !succeeded && !dead
                ? clock
                    .GetUtcNow()
                    .UtcDateTime.AddSeconds(
                        Math.Min(
                            3600,
                            Math.Pow(2, Math.Min(16, attempt - 1))
                                * Math.Max(1, _options.WebhookRetryBaseSeconds)
                        ) + Random.Shared.NextDouble()
                    )
                : null;
        var updated = delivery with
        {
            DeliveredAt = clock.GetUtcNow().UtcDateTime,
            StatusCode = statusCode,
            Succeeded = succeeded,
            Error = error,
            AttemptCount = attempt,
            LatencyMilliseconds = latency,
            Status =
                succeeded ? "succeeded"
                : dead ? "dead_letter"
                : "retrying",
            NextAttemptAt = next
        };
        var record = new WebhookDeliveryAttemptDocument
        {
            Id = Guid.NewGuid().ToString("n"),
            Network = delivery.Network,
            DeliveryId = delivery.Id,
            EventId = delivery.EventId,
            PayloadVersion = delivery.PayloadVersion,
            AttemptNumber = attempt,
            StatusCode = statusCode,
            Succeeded = succeeded,
            Retryable = retryable,
            Error = error,
            LatencyMilliseconds = latency,
            AttemptedAt = clock.GetUtcNow().UtcDateTime,
        };

        if (await deliveries.CompleteAsync(updated, record, cancellationToken))
            operations.RecordDelivery(succeeded, !succeeded && !dead, dead, latency);
    }

    public async Task<WebhookDeliveryDocument> QueueTestAsync(
        WebhookSubscriptionDocument subscription,
        CancellationToken cancellationToken
    )
    {
        var deliveryId = Guid.NewGuid().ToString("n");
        var eventId = $"test-{deliveryId}";
        var payload = WebhookPayloadContract.CreateTest(deliveryId, eventId, subscription, clock);
        var delivery = CreateQueuedDelivery(
            deliveryId,
            subscription,
            eventId,
            payload,
            $"test:{deliveryId}",
            subscription.EventName ?? "Pusharoo.Test",
            "test"
        );
        await deliveries.EnqueueAsync(delivery, cancellationToken);

        return delivery;
    }

    public async Task RedeliverAsync(
        WebhookSubscriptionDocument subscription,
        WebhookDeliveryDocument original,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(original.PayloadJson))
        {
            throw new InvalidOperationException(
                "The stored payload has expired. Block replay only recovers missing events; retained identities skip this delivery."
            );
        }

        var deliveryId = Guid.NewGuid().ToString("n");
        var payload = WebhookPayloadContract.ForDelivery(
            original.PayloadJson,
            deliveryId,
            original.EventId,
            manual: true
        );
        await deliveries.EnqueueAsync(
            new WebhookDeliveryDocument
            {
                Id = deliveryId,
                Network = subscription.Network,
                SubscriptionId = subscription.Id,
                EventId = original.EventId,
                WebhookUrl = subscription.WebhookUrl,
                PayloadJson = payload,
                PayloadVersion = WebhookPayloadContract.Version(payload),
                IdempotencyKey = $"manual:{original.Id}:{deliveryId}",
                EventName = original.EventName,
                Trigger = "manual",
                RedeliveryOfDeliveryId = original.Id,
                Status = "pending",
                AttemptCount = 0,
                DeliveredAt = clock.GetUtcNow().UtcDateTime,
                NextAttemptAt = clock.GetUtcNow().UtcDateTime,
            },
            cancellationToken
        );
    }

    private WebhookDeliveryDocument CreateQueuedDelivery(
        string id,
        WebhookSubscriptionDocument subscription,
        string eventId,
        string payloadJson,
        string idempotencyKey,
        string eventName,
        string trigger
    ) =>
        new()
        {
            Id = id,
            Network = subscription.Network,
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
            DeliveredAt = clock.GetUtcNow().UtcDateTime,
            NextAttemptAt = clock.GetUtcNow().UtcDateTime,
        };
}
