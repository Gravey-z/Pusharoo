using MongoDB.Driver;
using MongoDB.Driver.Linq;
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

    public async Task<WebhookDeliveryDocument?> GetLatestBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        return await db.Deliveries
            .Find(delivery => delivery.SubscriptionId == subscriptionId)
            .SortByDescending(delivery => delivery.DeliveredAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<WebhookDeliveryDocument?> GetByIdAsync(string deliveryId, CancellationToken cancellationToken)
    {
        return await db.Deliveries.Find(delivery => delivery.Id == deliveryId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken cancellationToken)
        => await db.Deliveries.Find(delivery => delivery.IdempotencyKey == idempotencyKey).AnyAsync(cancellationToken);

    public async Task<bool> EnqueueAsync(WebhookDeliveryDocument delivery, CancellationToken cancellationToken)
    {
        try { await db.Deliveries.InsertOneAsync(delivery, cancellationToken: cancellationToken); return true; }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey) { return false; }
    }

    public async Task<WebhookDeliveryDocument?> ClaimDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<WebhookDeliveryDocument>.Filter.And(
            Builders<WebhookDeliveryDocument>.Filter.In(item => item.Status, ["pending", "retrying"]),
            Builders<WebhookDeliveryDocument>.Filter.Lte(item => item.NextAttemptAt, now));
        var update = Builders<WebhookDeliveryDocument>.Update.Set(item => item.Status, "delivering").Set(item => item.LeaseUntil, now.AddMinutes(2));
        return await db.Deliveries.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<WebhookDeliveryDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken);
    }

    public async Task CompleteAsync(WebhookDeliveryDocument delivery, WebhookDeliveryAttemptDocument attempt, CancellationToken cancellationToken)
    {
        await db.DeliveryAttempts.InsertOneAsync(attempt, cancellationToken: cancellationToken);
        await db.Deliveries.ReplaceOneAsync(item => item.Id == delivery.Id, delivery, cancellationToken: cancellationToken);
    }
}
