using Microsoft.Extensions.Options;
using MongoDB.Bson;
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
        PaymentIntents = database.GetCollection<RelayPaymentIntentDocument>("relayPaymentIntents");
        Payments = database.GetCollection<RelayPaymentDocument>("relayPayments");
        EntitlementHistory = database.GetCollection<RelayEntitlementHistoryDocument>("relayEntitlementHistory");
        PaymentIntents.Indexes.CreateOne(new CreateIndexModel<RelayPaymentIntentDocument>(Builders<RelayPaymentIntentDocument>.IndexKeys.Ascending(x => x.ProjectId).Descending(x => x.CreatedAt)));
        var paymentIntentIndexes = PaymentIntents.Indexes.List().ToList();
        if (paymentIntentIndexes.Any(index => string.Equals(index.GetValue("name", string.Empty).AsString, "submittedTransactionId_1", StringComparison.Ordinal)))
        {
            PaymentIntents.Indexes.DropOne("submittedTransactionId_1");
        }
        PaymentIntents.Indexes.CreateOne(new CreateIndexModel<RelayPaymentIntentDocument>(
            Builders<RelayPaymentIntentDocument>.IndexKeys.Ascending(x => x.SubmittedTransactionId),
            new CreateIndexOptions<RelayPaymentIntentDocument>
            {
                Name = "submittedTransactionId_unique",
                Unique = true,
                PartialFilterExpression = new BsonDocument("submittedTransactionId", new BsonDocument("$type", "string"))
            }));
        Payments.Indexes.CreateOne(new CreateIndexModel<RelayPaymentDocument>(Builders<RelayPaymentDocument>.IndexKeys.Ascending(x => x.ProjectId).Descending(x => x.VerifiedAt)));
        EntitlementHistory.Indexes.CreateOne(new CreateIndexModel<RelayEntitlementHistoryDocument>(Builders<RelayEntitlementHistoryDocument>.IndexKeys.Ascending(x => x.PaymentId).Ascending(x => x.Network), new CreateIndexOptions { Unique = true }));
    }

    public IMongoCollection<WebhookSubscriptionDocument> Subscriptions { get; }

    public IMongoCollection<WebhookDeliveryDocument> Deliveries { get; }
    public IMongoCollection<WebhookDeliveryAttemptDocument> DeliveryAttempts { get; }
    public IMongoCollection<RelayEntitlementDocument> Entitlements { get; }
    public IMongoCollection<RelayPaymentIntentDocument> PaymentIntents { get; }
    public IMongoCollection<RelayPaymentDocument> Payments { get; }
    public IMongoCollection<RelayEntitlementHistoryDocument> EntitlementHistory { get; }

    public IMongoCollection<EventCheckpointDocument> Checkpoints { get; }
}
