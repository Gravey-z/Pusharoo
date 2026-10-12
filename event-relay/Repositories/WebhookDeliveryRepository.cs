using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public sealed class WebhookDeliveryRepository(MongoDbContext db) : IWebhookDeliveryRepository
{
    public async Task<IReadOnlyList<WebhookDeliveryDocument>> GetBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        return await db.Deliveries
            .Find(delivery => delivery.SubscriptionId == subscriptionId && delivery.Status != "deduplicated")
            .SortByDescending(delivery => delivery.DeliveredAt)
            .Limit(50)
            .ToListAsync(cancellationToken);
    }

    public async Task<WebhookDeliveryDocument?> GetLatestBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        return await db.Deliveries
            .Find(delivery => delivery.SubscriptionId == subscriptionId && delivery.Status != "deduplicated")
            .SortByDescending(delivery => delivery.DeliveredAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, WebhookDeliveryDocument>> GetLatestBySubscriptionIdsAsync(
        IReadOnlyCollection<string> subscriptionIds,
        CancellationToken cancellationToken)
    {
        if (subscriptionIds.Count == 0)
        {
            return new Dictionary<string, WebhookDeliveryDocument>();
        }

        var deliveries = await db.Deliveries
            .Find(Builders<WebhookDeliveryDocument>.Filter.In(item => item.SubscriptionId, subscriptionIds) & Builders<WebhookDeliveryDocument>.Filter.Ne(item => item.Status, "deduplicated"))
            .SortByDescending(item => item.DeliveredAt)
            .ToListAsync(cancellationToken);

        return deliveries
            .GroupBy(item => item.SubscriptionId)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public async Task<WebhookDeliveryDocument?> GetByIdAsync(string deliveryId, CancellationToken cancellationToken)
    {
        return await db.Deliveries.Find(delivery => delivery.Id == deliveryId && delivery.Status != "deduplicated").FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> EnqueueAsync(WebhookDeliveryDocument delivery, CancellationToken cancellationToken)
    {
        try { await db.Deliveries.WithWriteConcern(WriteConcern.WMajority.With(journal: true)).InsertOneAsync(delivery, cancellationToken: cancellationToken); return true; }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // A duplicate is successful only if the intended automatic/manual identity is already durable.
            // Do not mistake an unrelated _id collision or an uncommitted insert for queued work.
            var existing = await db.Deliveries.WithReadConcern(ReadConcern.Majority)
                .Find(x => x.IdempotencyKey == delivery.IdempotencyKey && x.SubscriptionId == delivery.SubscriptionId && x.EventId == delivery.EventId)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is null) throw;
            return false;
        }
    }

    public Task<long> CountOutstandingAsync(CancellationToken cancellationToken)
    {
        return db.Deliveries.CountDocumentsAsync(
            Builders<WebhookDeliveryDocument>.Filter.In(item => item.Status, ["pending", "retrying", "delivering"]),
            cancellationToken: cancellationToken);
    }

    public async Task<WebhookDeliveryDocument?> ClaimDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<WebhookDeliveryDocument>.Filter.Or(
            Builders<WebhookDeliveryDocument>.Filter.And(
                Builders<WebhookDeliveryDocument>.Filter.In(item => item.Status, ["pending", "retrying"]),
                Builders<WebhookDeliveryDocument>.Filter.Lte(item => item.NextAttemptAt, now)),
            Builders<WebhookDeliveryDocument>.Filter.And(
                Builders<WebhookDeliveryDocument>.Filter.Eq(item => item.Status, "delivering"),
                Builders<WebhookDeliveryDocument>.Filter.Lte(item => item.LeaseUntil, now)));
        var update = Builders<WebhookDeliveryDocument>.Update
            .Set(item => item.Status, "delivering")
            .Set(item => item.LeaseUntil, now.AddMinutes(2));
        return await db.Deliveries.FindOneAndUpdateAsync(filter, update, new FindOneAndUpdateOptions<WebhookDeliveryDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken);
    }

    public async Task CompleteAsync(WebhookDeliveryDocument delivery, WebhookDeliveryAttemptDocument attempt, CancellationToken cancellationToken)
    {
        var result = await db.Deliveries.ReplaceOneAsync(
            item => item.Id == delivery.Id,
            delivery,
            cancellationToken: cancellationToken);
        if (result.MatchedCount == 0)
        {
            return;
        }

        await db.DeliveryAttempts.InsertOneAsync(attempt, cancellationToken: cancellationToken);
    }

    public async Task PurgeExpiredAsync(DateTime payloadCutoff, DateTime historyCutoff, CancellationToken cancellationToken)
    {
        await db.Deliveries.UpdateManyAsync(
            Builders<WebhookDeliveryDocument>.Filter.And(
                Builders<WebhookDeliveryDocument>.Filter.Lt(item => item.DeliveredAt, payloadCutoff),
                Builders<WebhookDeliveryDocument>.Filter.Ne(item => item.PayloadJson, null),
                Builders<WebhookDeliveryDocument>.Filter.In(item => item.Status, ["succeeded", "dead_letter", "cancelled"])),
            Builders<WebhookDeliveryDocument>.Update.Unset(item => item.PayloadJson), cancellationToken: cancellationToken);

        var expired = await db.Deliveries.Find(Builders<WebhookDeliveryDocument>.Filter.And(
            Builders<WebhookDeliveryDocument>.Filter.Lt(item => item.DeliveredAt, historyCutoff),
            Builders<WebhookDeliveryDocument>.Filter.In(item => item.Status, ["succeeded", "dead_letter", "cancelled"])))
            .Project(item => item.Id).ToListAsync(cancellationToken);
        if (expired.Count == 0) return;
        await db.DeliveryAttempts.DeleteManyAsync(item => expired.Contains(item.DeliveryId), cancellationToken);
        // Retain automatic keys even when history expires: a stalled, partially queued block can outlive retention.
        // Removing the unique key here would allow a restart to recreate an automatic delivery.
        await db.Deliveries.UpdateManyAsync(item => expired.Contains(item.Id) && item.Trigger == "automatic",
            Builders<WebhookDeliveryDocument>.Update.Set(item => item.Status, "deduplicated")
                .Unset(item => item.PayloadJson).Unset(item => item.Error).Unset(item => item.WebhookUrl)
                .Unset(item => item.NextAttemptAt).Unset(item => item.LeaseUntil), cancellationToken: cancellationToken);
        await db.Deliveries.DeleteManyAsync(item => expired.Contains(item.Id) && item.Trigger != "automatic", cancellationToken);
    }

    public async Task DeleteBySubscriptionIdsAsync(IReadOnlyList<string> subscriptionIds, CancellationToken cancellationToken)
    {
        if (subscriptionIds.Count == 0) return;
        var deliveryIds = await db.Deliveries.Find(item => subscriptionIds.Contains(item.SubscriptionId)).Project(item => item.Id).ToListAsync(cancellationToken);
        if (deliveryIds.Count > 0) await db.DeliveryAttempts.DeleteManyAsync(item => deliveryIds.Contains(item.DeliveryId), cancellationToken);
        await db.Deliveries.DeleteManyAsync(item => subscriptionIds.Contains(item.SubscriptionId), cancellationToken);
    }
}
