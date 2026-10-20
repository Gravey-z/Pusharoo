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

        ProjectAdmissions = database.GetCollection<RelayProjectAdmissionDocument>("relayProjectAdmissions")
            .WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        ProjectAdmissions.Indexes.CreateOne(new CreateIndexModel<RelayProjectAdmissionDocument>(
            Builders<RelayProjectAdmissionDocument>.IndexKeys.Ascending(x => x.NextCheckAt)));
        ProjectAdmissions.Indexes.CreateOne(new CreateIndexModel<RelayProjectAdmissionDocument>(
            Builders<RelayProjectAdmissionDocument>.IndexKeys.Ascending("mainnet.periodId")));
        ProjectAdmissions.Indexes.CreateOne(new CreateIndexModel<RelayProjectAdmissionDocument>(
            Builders<RelayProjectAdmissionDocument>.IndexKeys.Ascending("testnet.periodId")));

        PurchasedPeriods = database.GetCollection<RelayPurchasedPeriodDocument>("relayPurchasedPeriods").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        PeriodUpgrades = database.GetCollection<RelayPeriodUpgradeDocument>("relayPeriodUpgrades").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        BillingDecisions = database.GetCollection<RelayBillingDecisionDocument>("relayBillingDecisions").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        GrantProgress = database.GetCollection<RelayGrantProgressDocument>("relayGrantProgress").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        BillingSchema = database.GetCollection<BsonDocument>("relayBillingSchema").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        LegacyBillingSnapshots = database.GetCollection<BsonDocument>("relayLegacyBillingSnapshots").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        BillingLedgers = database.GetCollection<RelayBillingLedgerDocument>("relayBillingLedgers").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        PaymentResolutions = database.GetCollection<RelayPaymentResolutionDocument>("relayPaymentResolutions").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        PaymentTransactions = database.GetCollection<BsonDocument>("relayPaymentTransactions").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        PaymentTransactions.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            new BsonDocument("intentId", 1), new CreateIndexOptions<BsonDocument> { Name = "one_incoming_transaction_per_intent", Unique = true,
                PartialFilterExpression = new BsonDocument("kind", "payment") }));
        PaymentTransactions.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "kind", 1 }, { "bindingComplete", 1 }, { "claimedAt", 1 } }, new CreateIndexOptions { Name = "payment_binding_recovery" }));
        PurchasedPeriods.Indexes.CreateOne(new CreateIndexModel<RelayPurchasedPeriodDocument>(
            Builders<RelayPurchasedPeriodDocument>.IndexKeys.Ascending(x => x.ProjectId).Ascending(x => x.PurchaseId),
            new CreateIndexOptions { Name = "period_purchase_unique", Unique = true }));
        PurchasedPeriods.Indexes.CreateOne(new CreateIndexModel<RelayPurchasedPeriodDocument>(
            Builders<RelayPurchasedPeriodDocument>.IndexKeys.Ascending(x => x.ProjectId).Ascending(x => x.StartsAt)));
        PeriodUpgrades.Indexes.CreateOne(new CreateIndexModel<RelayPeriodUpgradeDocument>(
            Builders<RelayPeriodUpgradeDocument>.IndexKeys.Ascending(x => x.ProjectId).Ascending(x => x.Target.PeriodId).Ascending(x => x.Target.ExpectedVersion),
            new CreateIndexOptions { Name = "period_upgrade_version_unique", Unique = true }));
        BillingDecisions.Indexes.CreateOne(new CreateIndexModel<RelayBillingDecisionDocument>(
            Builders<RelayBillingDecisionDocument>.IndexKeys.Ascending(x => x.IntentId),
            new CreateIndexOptions { Name = "decision_intent_unique", Unique = true }));
        GrantProgress.Indexes.CreateOne(new CreateIndexModel<RelayGrantProgressDocument>(
            Builders<RelayGrantProgressDocument>.IndexKeys.Ascending(x => x.DecisionId).Ascending(x => x.AllocationId),
            new CreateIndexOptions { Name = "grant_allocation_unique", Unique = true }));

        Subscriptions = database.GetCollection<WebhookSubscriptionDocument>("eventSubscriptions");
        Deliveries = database.GetCollection<WebhookDeliveryDocument>("webhookDeliveries");
        DeliveryAttempts = database.GetCollection<WebhookDeliveryAttemptDocument>(
            "webhookDeliveryAttempts"
        );
        AllowancePeriods = database.GetCollection<RelayAllowancePeriodDocument>(
            "relayAllowancePeriods"
        );
        ReplayJobs = database.GetCollection<ReplayJobDocument>("eventReplayJobs");
        ReplayAdmissions = database
            .GetCollection<BsonDocument>("eventReplayAdmissions")
            .WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        ReplayJobs.Indexes.CreateOne(
            new CreateIndexModel<ReplayJobDocument>(
                Builders<ReplayJobDocument>
                    .IndexKeys.Ascending(x => x.Network)
                    .Ascending(x => x.Status)
                    .Ascending(x => x.CreatedAt)
            )
        );
        ReplayJobs.Indexes.CreateOne(
            new CreateIndexModel<ReplayJobDocument>(
                Builders<ReplayJobDocument>.IndexKeys.Ascending(x => x.SubscriptionId),
                new CreateIndexOptions<ReplayJobDocument>
                {
                    Name = "one_active_replay_per_subscription",
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("active", true)
                }
            )
        );
        Entitlements = database.GetCollection<RelayEntitlementDocument>("relayEntitlements").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        Entitlements.Indexes.CreateOne(
            new CreateIndexModel<RelayEntitlementDocument>(
                Builders<RelayEntitlementDocument>
                    .IndexKeys.Ascending(x => x.ProjectId)
                    .Ascending(x => x.Network),
                new CreateIndexOptions { Unique = true }
            )
        );
        Deliveries.Indexes.CreateOne(
            new CreateIndexModel<WebhookDeliveryDocument>(
                Builders<WebhookDeliveryDocument>.IndexKeys.Ascending(delivery =>
                    delivery.IdempotencyKey
                ),
                new CreateIndexOptions { Unique = true, Sparse = true }
            )
        );
        Deliveries.Indexes.CreateOne(
            new CreateIndexModel<WebhookDeliveryDocument>(
                Builders<WebhookDeliveryDocument>
                    .IndexKeys.Ascending(x => x.SubscriptionId)
                    .Ascending(x => x.EventId),
                new CreateIndexOptions<WebhookDeliveryDocument>
                {
                    Name = "automatic_subscription_event_unique",
                    Unique = true,
                    PartialFilterExpression = new BsonDocument
                    {
                        { "trigger", "automatic" },
                        { "subscriptionId", new BsonDocument("$type", "string") },
                        { "eventId", new BsonDocument("$type", "string") }
                    }
                }
            )
        );
        Deliveries.Indexes.CreateOne(
            new CreateIndexModel<WebhookDeliveryDocument>(
                Builders<WebhookDeliveryDocument>
                    .IndexKeys.Ascending(x => x.Network)
                    .Ascending(x => x.Status)
                    .Ascending(x => x.DeliveredAt),
                new CreateIndexOptions { Name = "network_status_history" }
            )
        );
        Checkpoints = database.GetCollection<EventCheckpointDocument>("eventCheckpoints");
        PaymentIntents = database.GetCollection<RelayPaymentIntentDocument>("relayPaymentIntents").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        Payments = database.GetCollection<RelayPaymentDocument>("relayPayments").WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        EntitlementHistory = database.GetCollection<RelayEntitlementHistoryDocument>(
            "relayEntitlementHistory"
        ).WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        PaymentIntents.Indexes.CreateOne(
            new CreateIndexModel<RelayPaymentIntentDocument>(
                Builders<RelayPaymentIntentDocument>
                    .IndexKeys.Ascending(x => x.ProjectId)
                    .Descending(x => x.CreatedAt)
            )
        );
        var paymentIntentIndexes = PaymentIntents.Indexes.List().ToList();

        if (
            paymentIntentIndexes.Any(index =>
                string.Equals(
                    index.GetValue("name", string.Empty).AsString,
                    "submittedTransactionId_1",
                    StringComparison.Ordinal
                )
            )
        )
        {
            PaymentIntents.Indexes.DropOne("submittedTransactionId_1");
        }

        PaymentIntents.Indexes.CreateOne(
            new CreateIndexModel<RelayPaymentIntentDocument>(
                Builders<RelayPaymentIntentDocument>.IndexKeys.Ascending(x =>
                    x.SubmittedTransactionId
                ),
                new CreateIndexOptions<RelayPaymentIntentDocument>
                {
                    Name = "submittedTransactionId_unique",
                    Unique = true,
                    PartialFilterExpression = new BsonDocument(
                        "submittedTransactionId",
                        new BsonDocument("$type", "string")
                    )
                }
            )
        );
        Payments.Indexes.CreateOne(
            new CreateIndexModel<RelayPaymentDocument>(
                Builders<RelayPaymentDocument>
                    .IndexKeys.Ascending(x => x.ProjectId)
                    .Descending(x => x.VerifiedAt)
            )
        );
        PaymentIntents.Indexes.CreateOne(new CreateIndexModel<RelayPaymentIntentDocument>(
            Builders<RelayPaymentIntentDocument>.IndexKeys.Ascending(x => x.ProjectId).Ascending(x => x.RequestKey),
            new CreateIndexOptions<RelayPaymentIntentDocument> { Name = "project_request_unique", Unique = true,
                PartialFilterExpression = new BsonDocument("requestKey", new BsonDocument("$type", "string")) }));
        PaymentIntents.Indexes.CreateOne(new CreateIndexModel<RelayPaymentIntentDocument>(
            Builders<RelayPaymentIntentDocument>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.NextRecoveryAt).Ascending(x => x.CreatedAt),
            new CreateIndexOptions { Name = "payment_recovery_due" }));
        PaymentResolutions.Indexes.CreateOne(new CreateIndexModel<RelayPaymentResolutionDocument>(
            Builders<RelayPaymentResolutionDocument>.IndexKeys.Ascending(x => x.IntentId),
            new CreateIndexOptions { Name = "resolution_intent_lookup" }));
        EntitlementHistory.Indexes.CreateOne(
            new CreateIndexModel<RelayEntitlementHistoryDocument>(
                Builders<RelayEntitlementHistoryDocument>
                    .IndexKeys.Ascending(x => x.PaymentId)
                    .Ascending(x => x.Network).Ascending(x => x.AllocationId),
                new CreateIndexOptions { Name = "payment_network_allocation_unique", Unique = true }
            )
        );
        // Create the stronger index before removing its old two-column predecessor.
        // Billing writers must be quiesced during this additive schema rollout.
        if (EntitlementHistory.Indexes.List().ToList().Any(x => x["name"] == "paymentId_1_network_1"))
        {
            try { EntitlementHistory.Indexes.DropOne("paymentId_1_network_1"); }
            catch (MongoCommandException error) when (error.Code == 27) { } // another instance completed the same migration
        }
    }

    public IMongoCollection<WebhookSubscriptionDocument> Subscriptions { get; }

    public IMongoCollection<RelayPurchasedPeriodDocument> PurchasedPeriods { get; }
    public IMongoCollection<RelayPeriodUpgradeDocument> PeriodUpgrades { get; }
    public IMongoCollection<RelayBillingDecisionDocument> BillingDecisions { get; }
    public IMongoCollection<RelayGrantProgressDocument> GrantProgress { get; }
    public IMongoCollection<BsonDocument> BillingSchema { get; }
    public IMongoCollection<BsonDocument> LegacyBillingSnapshots { get; }
    public IMongoCollection<RelayBillingLedgerDocument> BillingLedgers { get; }
    public IMongoCollection<RelayPaymentResolutionDocument> PaymentResolutions { get; }
    public IMongoCollection<BsonDocument> PaymentTransactions { get; }

    public IMongoCollection<RelayAllowancePeriodDocument> AllowancePeriods { get; }
    public IMongoCollection<RelayProjectAdmissionDocument> ProjectAdmissions { get; }

    public IMongoCollection<ReplayJobDocument> ReplayJobs { get; }

    public IMongoCollection<BsonDocument> ReplayAdmissions { get; }

    public IMongoCollection<WebhookDeliveryDocument> Deliveries { get; }

    public IMongoCollection<WebhookDeliveryAttemptDocument> DeliveryAttempts { get; }

    public IMongoCollection<RelayEntitlementDocument> Entitlements { get; }

    public IMongoCollection<RelayPaymentIntentDocument> PaymentIntents { get; }

    public IMongoCollection<RelayPaymentDocument> Payments { get; }

    public IMongoCollection<RelayEntitlementHistoryDocument> EntitlementHistory { get; }

    public IMongoCollection<EventCheckpointDocument> Checkpoints { get; }
}
