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
        WebhookAuthorizationNonces = database.GetCollection<WebhookAuthorizationNonceDocument>("webhookAuthorizationNonces");

        WebhookAuthorizationNonces.Indexes.CreateOne(
            new CreateIndexModel<WebhookAuthorizationNonceDocument>(
                Builders<WebhookAuthorizationNonceDocument>.IndexKeys.Ascending(nonce => nonce.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
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
        Deployments.Indexes.CreateOne(new CreateIndexModel<DeploymentDocument>(
            Builders<DeploymentDocument>.IndexKeys.Ascending(deployment => deployment.TransactionId),
            new CreateIndexOptions { Unique = true, Sparse = true }));
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

    public IMongoCollection<WebhookAuthorizationNonceDocument> WebhookAuthorizationNonces { get; }
}
