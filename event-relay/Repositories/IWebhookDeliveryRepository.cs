using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Repositories;

public interface IWebhookDeliveryRepository
{
    Task InsertAsync(WebhookDeliveryDocument delivery, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookDeliveryDocument>> GetBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken);
}
