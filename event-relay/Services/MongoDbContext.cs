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
        Checkpoints = database.GetCollection<EventCheckpointDocument>("eventCheckpoints");
    }

    public IMongoCollection<WebhookSubscriptionDocument> Subscriptions { get; }

    public IMongoCollection<WebhookDeliveryDocument> Deliveries { get; }

    public IMongoCollection<EventCheckpointDocument> Checkpoints { get; }
}
