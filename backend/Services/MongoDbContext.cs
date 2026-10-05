using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed class MongoDbContext
{
    public MongoDbContext(IOptions<MongoDbOptions> options)
    {
        var mongoOptions = options.Value;
        var client = new MongoClient(mongoOptions.ConnectionString);
        var database = client.GetDatabase(mongoOptions.DatabaseName);

        Projects = database.GetCollection<ProjectDocument>("projects");
        ContractArtifacts = database.GetCollection<ArtifactDocument>("contractArtifacts");
        Deployments = database.GetCollection<DeploymentDocument>("deployments");
        WebhookSubscriptions = database.GetCollection<BsonDocument>("eventSubscriptions");
        WebhookDeliveries = database.GetCollection<BsonDocument>("webhookDeliveries");
        WebhookDeliveryAttempts = database.GetCollection<BsonDocument>("webhookDeliveryAttempts");
        RelayEntitlements = database.GetCollection<BsonDocument>("relayEntitlements");
        RelayPaymentIntents = database.GetCollection<BsonDocument>("relayPaymentIntents");
        RelayPayments = database.GetCollection<BsonDocument>("relayPayments");
        RelayEntitlementHistory = database.GetCollection<BsonDocument>("relayEntitlementHistory");
        FaucetClaims = database.GetCollection<FaucetClaimDocument>("faucetClaims");
        FaucetDailyFeeBudgets = database.GetCollection<FaucetDailyFeeBudgetDocument>("faucetDailyFeeBudgets");
        WalletLoginChallenges = database.GetCollection<WalletLoginChallengeDocument>("walletLoginChallenges");
        WalletLoginSessions = database.GetCollection<WalletLoginSessionDocument>("walletLoginSessions");

        WalletLoginChallenges.Indexes.CreateOne(new CreateIndexModel<WalletLoginChallengeDocument>(
            Builders<WalletLoginChallengeDocument>.IndexKeys.Ascending(challenge => challenge.ExpiresAtUtc),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
        WalletLoginSessions.Indexes.CreateOne(new CreateIndexModel<WalletLoginSessionDocument>(
            Builders<WalletLoginSessionDocument>.IndexKeys.Ascending(session => session.ExpiresAtUtc),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));

        FaucetClaims.Indexes.CreateMany([
            new CreateIndexModel<FaucetClaimDocument>(
                Builders<FaucetClaimDocument>.IndexKeys.Ascending(claim => claim.ActiveWalletKey),
                new CreateIndexOptions { Unique = true, Sparse = true }),
            new CreateIndexModel<FaucetClaimDocument>(
                Builders<FaucetClaimDocument>.IndexKeys.Descending(claim => claim.CreatedAt))
        ]);
        Projects.Indexes.CreateOne(new CreateIndexModel<ProjectDocument>(
            Builders<ProjectDocument>.IndexKeys.Ascending(project => project.IdempotencyKey),
            new CreateIndexOptions { Unique = true, Sparse = true }));
        ContractArtifacts.Indexes.CreateMany([
            new CreateIndexModel<ArtifactDocument>(
                Builders<ArtifactDocument>.IndexKeys
                    .Ascending(artifact => artifact.ProjectId)
                    .Ascending(artifact => artifact.Version),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ArtifactDocument>(
                Builders<ArtifactDocument>.IndexKeys.Ascending(artifact => artifact.IdempotencyKey),
                new CreateIndexOptions { Unique = true, Sparse = true })
        ]);
        Deployments.Indexes.CreateMany([
            new CreateIndexModel<DeploymentDocument>(
                Builders<DeploymentDocument>.IndexKeys.Ascending(deployment => deployment.TransactionId),
                new CreateIndexOptions { Unique = true, Sparse = true }),
            new CreateIndexModel<DeploymentDocument>(
                Builders<DeploymentDocument>.IndexKeys
                    .Ascending(deployment => deployment.ProjectId)
                    .Descending(deployment => deployment.CreatedAt)),
            new CreateIndexModel<DeploymentDocument>(
                Builders<DeploymentDocument>.IndexKeys.Ascending(deployment => deployment.ActiveAttemptKey),
                new CreateIndexOptions { Unique = true, Sparse = true })
        ]);
    }

    public IMongoCollection<ProjectDocument> Projects { get; }

    public IMongoCollection<ArtifactDocument> ContractArtifacts { get; }

    public IMongoCollection<DeploymentDocument> Deployments { get; }

    public IMongoCollection<BsonDocument> WebhookSubscriptions { get; }

    public IMongoCollection<BsonDocument> WebhookDeliveries { get; }

    public IMongoCollection<BsonDocument> WebhookDeliveryAttempts { get; }

    public IMongoCollection<BsonDocument> RelayEntitlements { get; }
    public IMongoCollection<BsonDocument> RelayPaymentIntents { get; }
    public IMongoCollection<BsonDocument> RelayPayments { get; }
    public IMongoCollection<BsonDocument> RelayEntitlementHistory { get; }

    public IMongoCollection<FaucetClaimDocument> FaucetClaims { get; }

    public IMongoCollection<FaucetDailyFeeBudgetDocument> FaucetDailyFeeBudgets { get; }

    public IMongoCollection<WalletLoginChallengeDocument> WalletLoginChallenges { get; }

    public IMongoCollection<WalletLoginSessionDocument> WalletLoginSessions { get; }

}
