using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public interface IWebhookTransport
{
    // Payload validation and identity preparation are performed by the delivery processor.
    Task<WebhookTransportResult> SendAsync(
        WebhookSubscriptionDocument subscription,
        WebhookDeliveryDocument delivery,
        CancellationToken cancellationToken
    );
}
