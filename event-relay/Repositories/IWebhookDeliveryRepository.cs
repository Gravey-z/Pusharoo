using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Repositories;

public interface IWebhookDeliveryRepository
{
    Task InsertAsync(WebhookDeliveryDocument delivery, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookDeliveryDocument>> GetBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryDocument?> GetLatestBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryDocument?> GetByIdAsync(string deliveryId, CancellationToken cancellationToken);
}
