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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.WebhookTimeoutSeconds)));

        int? statusCode = null;
        string? error = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.WebhookUrl);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Delivery", deliveryId);
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Event", observedEvent.EventName);

            if (!string.IsNullOrWhiteSpace(subscription.Secret))
            {
                request.Headers.TryAddWithoutValidation(
                    "X-Pusharoo-Signature",
                    WebhookSignature.Create(subscription.Secret, json));
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
                    EventId = observedEvent.Id,
                    WebhookUrl = subscription.WebhookUrl,
                    StatusCode = code,
                    Succeeded = succeeded,
                    Error = failure,
                    DeliveredAt = DateTime.UtcNow
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
