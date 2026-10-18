using System.Text;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookTransport(
    HttpClient httpClient,
    WebhookSecretProtector secretProtector,
    IOptions<EventRelayOptions> options,
    TimeProvider? timeProvider = null
) : IWebhookTransport
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<WebhookTransportResult> SendAsync(
        WebhookSubscriptionDocument subscription,
        WebhookDeliveryDocument delivery,
        CancellationToken cancellationToken
    )
    {
        int? statusCode = null;
        string? error = null;
        var succeeded = false;
        var retryable = true;

        try
        {
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(1, options.Value.WebhookTimeoutSeconds)),
                clock
            );
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token
            );
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.WebhookUrl)
            {
                Content = new StringContent(
                    delivery.PayloadJson ?? "{}",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Delivery", delivery.Id);
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Event", delivery.EventName);
            request.Headers.TryAddWithoutValidation("X-Pusharoo-Event-Id", delivery.EventId);
            request.Headers.TryAddWithoutValidation(
                "X-Pusharoo-Payload-Version",
                delivery.PayloadVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            var secret = secretProtector.Unprotect(subscription.Secret);

            if (!string.IsNullOrWhiteSpace(secret))
                request.Headers.TryAddWithoutValidation(
                    "X-Pusharoo-Signature",
                    WebhookSignature.Create(secret, delivery.PayloadJson ?? "{}")
                );

            foreach (var (key, value) in subscription.Headers)
                if (
                    !WebhookPayloadContract.IsTransportHeader(key)
                    && !request.Headers.TryAddWithoutValidation(key, value)
                )
                    request.Content.Headers.TryAddWithoutValidation(key, value);

            using var response = await httpClient.SendAsync(request, timeout.Token);
            statusCode = (int)response.StatusCode;
            succeeded = statusCode is >= 200 and <= 299;
            retryable = !succeeded && (statusCode is 408 or 425 or 429 || statusCode >= 500);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested
            )
        {
            error = ex.Message;
            retryable = ex is not FormatException;
        }

        return new WebhookTransportResult(succeeded, retryable, statusCode, error);
    }
}
