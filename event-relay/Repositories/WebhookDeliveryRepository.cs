using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public sealed class WebhookDeliveryRepository(MongoDbContext db) : IWebhookDeliveryRepository
{
    public async Task InsertAsync(WebhookDeliveryDocument delivery, CancellationToken cancellationToken)
    {
        await db.Deliveries.InsertOneAsync(delivery, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookDeliveryDocument>> GetBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        return await db.Deliveries
            .Find(delivery => delivery.SubscriptionId == subscriptionId)
            .SortByDescending(delivery => delivery.DeliveredAt)
            .Limit(50)
            .ToListAsync(cancellationToken);
    }
}
