using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Repositories;

public interface IWebhookSubscriptionRepository
{
    Task InsertAsync(WebhookSubscriptionDocument subscription, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookSubscriptionDocument>> GetAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookSubscriptionDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDocument?> GetByIdAsync(string subscriptionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookSubscriptionDocument>> GetMatchingAsync(
        string network,
        string contractHash,
        string eventName,
        CancellationToken cancellationToken);

    Task<bool> ReplaceAsync(WebhookSubscriptionDocument subscription, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string subscriptionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> DeleteExpiredAsync(DateTime now, CancellationToken cancellationToken);
}
