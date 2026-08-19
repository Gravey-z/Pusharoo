using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Repositories;

public interface IWebhookDeliveryRepository
{
    Task<IReadOnlyList<WebhookDeliveryDocument>> GetBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryDocument?> GetLatestBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, WebhookDeliveryDocument>> GetLatestBySubscriptionIdsAsync(
        IReadOnlyCollection<string> subscriptionIds,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryDocument?> GetByIdAsync(string deliveryId, CancellationToken cancellationToken);

    Task<bool> EnqueueAsync(WebhookDeliveryDocument delivery, CancellationToken cancellationToken);
    Task<long> CountOutstandingAsync(CancellationToken cancellationToken);
    Task<WebhookDeliveryDocument?> ClaimDueAsync(CancellationToken cancellationToken);
    Task CompleteAsync(WebhookDeliveryDocument delivery, WebhookDeliveryAttemptDocument attempt, CancellationToken cancellationToken);
    Task PurgeExpiredAsync(DateTime payloadCutoff, DateTime historyCutoff, CancellationToken cancellationToken);
    Task DeleteBySubscriptionIdsAsync(IReadOnlyList<string> subscriptionIds, CancellationToken cancellationToken);
}
