using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public sealed class WebhookSubscriptionRepository(MongoDbContext db) : IWebhookSubscriptionRepository
{
    public async Task InsertAsync(WebhookSubscriptionDocument subscription, CancellationToken cancellationToken)
    {
        await db.Subscriptions.InsertOneAsync(subscription, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookSubscriptionDocument>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await db.Subscriptions
            .Find(Builders<WebhookSubscriptionDocument>.Filter.Empty)
            .SortByDescending(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookSubscriptionDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        return await db.Subscriptions
            .Find(subscription => subscription.ProjectId == projectId)
            .SortByDescending(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<WebhookSubscriptionDocument?> GetByIdAsync(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        return await db.Subscriptions
            .Find(subscription => subscription.Id == subscriptionId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookSubscriptionDocument>> GetMatchingAsync(
        string network,
        string contractHash,
        string eventName,
        CancellationToken cancellationToken)
    {
        var filter = Builders<WebhookSubscriptionDocument>.Filter.And(
            Builders<WebhookSubscriptionDocument>.Filter.Eq(subscription => subscription.IsEnabled, true),
            Builders<WebhookSubscriptionDocument>.Filter.Ne(subscription => subscription.ProjectId, null),
            Builders<WebhookSubscriptionDocument>.Filter.Eq(subscription => subscription.Network, network),
            Builders<WebhookSubscriptionDocument>.Filter.Eq(subscription => subscription.ContractHash, contractHash),
            Builders<WebhookSubscriptionDocument>.Filter.Or(
                Builders<WebhookSubscriptionDocument>.Filter.Eq(subscription => subscription.EventName, null),
                Builders<WebhookSubscriptionDocument>.Filter.Eq(subscription => subscription.EventName, eventName)));

        return await db.Subscriptions
            .Find(filter)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ReplaceAsync(WebhookSubscriptionDocument subscription, CancellationToken cancellationToken)
    {
        var result = await db.Subscriptions.ReplaceOneAsync(
            item => item.Id == subscription.Id,
            subscription,
            cancellationToken: cancellationToken);

        return result.ModifiedCount == 1;
    }

    public async Task<bool> DeleteAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        var result = await db.Subscriptions.DeleteOneAsync(
            subscription => subscription.Id == subscriptionId,
            cancellationToken);

        return result.DeletedCount == 1;
    }

    public async Task<IReadOnlyList<string>> DeleteExpiredAsync(DateTime now, CancellationToken cancellationToken)
    {
        var filter = Builders<WebhookSubscriptionDocument>.Filter.And(
            Builders<WebhookSubscriptionDocument>.Filter.Eq(item => item.Network, "neo3:testnet"),
            Builders<WebhookSubscriptionDocument>.Filter.Lte(item => item.ExpiresAt, now));
        var ids = await db.Subscriptions.Find(filter).Project(item => item.Id).ToListAsync(cancellationToken);
        if (ids.Count > 0) await db.Subscriptions.DeleteManyAsync(filter, cancellationToken);
        return ids;
    }

}
