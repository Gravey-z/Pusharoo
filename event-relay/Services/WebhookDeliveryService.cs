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
    WebhookSecretProtector secretProtector,
    IOptions<EventRelayOptions> options,
    ILogger<WebhookDeliveryService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EventRelayOptions _options = options.Value;

    public async Task DeliverAsync(
        WebhookSubscriptionDocument subscription,
        ObservedNeoEvent observedEvent,
        CancellationToken cancellationToken)
    {
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
        await DeliverPayloadAsync(subscription, deliveryId, observedEvent.Id, json, observedEvent.Network,
            observedEvent.ContractHash, observedEvent.EventName, cancellationToken);
    }

    public async Task DeliverTestAsync(WebhookSubscriptionDocument subscription, CancellationToken cancellationToken)
    {
        var deliveryId = Guid.NewGuid().ToString("n");
        var payload = new WebhookPayload(deliveryId, subscription.Id, subscription.Network, 0,
            "test-event", subscription.ContractHash, subscription.EventName ?? "Pusharoo.Test",
            JsonSerializer.SerializeToElement(new { test = true, message = "Pusharoo test delivery" }), DateTime.UtcNow);
        await DeliverPayloadAsync(subscription, deliveryId, $"test-{deliveryId}", JsonSerializer.Serialize(payload, JsonOptions),
            subscription.Network, subscription.ContractHash, subscription.EventName ?? "Pusharoo.Test", cancellationToken);
    }

    public async Task RedeliverAsync(WebhookSubscriptionDocument subscription, WebhookDeliveryDocument original, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(original.PayloadJson))
        {
            throw new InvalidOperationException("This delivery was recorded before payload replay was available.");
        }

        var deliveryId = Guid.NewGuid().ToString("n");
        await DeliverPayloadAsync(subscription, deliveryId, original.EventId, original.PayloadJson,
            subscription.Network, subscription.ContractHash, "redelivery", cancellationToken);
    }

    private async Task DeliverPayloadAsync(WebhookSubscriptionDocument subscription, string deliveryId, string eventId,
        string json, string network, string contractHash, string eventName, CancellationToken cancellationToken)
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

            await RecordAsync(statusCode.Value is >= 200 and <= 299, null, statusCode, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = ex.Message;
            logger.LogWarning(
                ex,
                "Webhook delivery {DeliveryId} failed for subscription {SubscriptionId}.",
                deliveryId,
                subscription.Id);

            await RecordAsync(false, error, statusCode, cancellationToken);
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
