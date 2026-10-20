using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public interface IWebhookTransport
{
    Task<WebhookTransportResult> SendAsync(
        WebhookSubscriptionDocument subscription,
        WebhookDeliveryDocument delivery,
        CancellationToken cancellationToken
    );
}
