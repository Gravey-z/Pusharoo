using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class MongoDbContext
{
    public MongoDbContext(IOptions<MongoDbOptions> options)
    {
        var mongoOptions = options.Value;
        var client = new MongoClient(mongoOptions.ConnectionString);
        var database = client.GetDatabase(mongoOptions.DatabaseName);

        Subscriptions = database.GetCollection<WebhookSubscriptionDocument>("eventSubscriptions");
        Deliveries = database.GetCollection<WebhookDeliveryDocument>("webhookDeliveries");
        DeliveryAttempts = database.GetCollection<WebhookDeliveryAttemptDocument>("webhookDeliveryAttempts");
        Entitlements = database.GetCollection<RelayEntitlementDocument>("relayEntitlements");
        Entitlements.Indexes.CreateOne(new CreateIndexModel<RelayEntitlementDocument>(Builders<RelayEntitlementDocument>.IndexKeys.Ascending(x => x.ProjectId).Ascending(x => x.Network), new CreateIndexOptions { Unique = true }));
        Deliveries.Indexes.CreateOne(new CreateIndexModel<WebhookDeliveryDocument>(
            Builders<WebhookDeliveryDocument>.IndexKeys.Ascending(delivery => delivery.IdempotencyKey),
            new CreateIndexOptions { Unique = true, Sparse = true }));
        Checkpoints = database.GetCollection<EventCheckpointDocument>("eventCheckpoints");
    }

    public IMongoCollection<WebhookSubscriptionDocument> Subscriptions { get; }

    public IMongoCollection<WebhookDeliveryDocument> Deliveries { get; }
    public IMongoCollection<WebhookDeliveryAttemptDocument> DeliveryAttempts { get; }
    public IMongoCollection<RelayEntitlementDocument> Entitlements { get; }

    public IMongoCollection<EventCheckpointDocument> Checkpoints { get; }
}
