using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public sealed class WebhookDeliveryRepository(
    MongoDbContext db,
    IOptions<NeoRpcOptions>? options = null,
    TimeProvider? timeProvider = null
) : IWebhookDeliveryRepository
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<WebhookDeliveryDocument>> GetBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        return await db
            .Deliveries.Find(delivery =>
                delivery.SubscriptionId == subscriptionId && delivery.Status != "deduplicated"
            )
            .SortByDescending(delivery => delivery.DeliveredAt)
            .Limit(50)
            .ToListAsync(cancellationToken);
    }

    public async Task<WebhookDeliveryDocument?> GetLatestBySubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        return await db
            .Deliveries.Find(delivery =>
                delivery.SubscriptionId == subscriptionId && delivery.Status != "deduplicated"
            )
            .SortByDescending(delivery => delivery.DeliveredAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<
        IReadOnlyDictionary<string, WebhookDeliveryDocument>
    > GetLatestBySubscriptionIdsAsync(
        IReadOnlyCollection<string> subscriptionIds,
        CancellationToken cancellationToken
    )
    {
        if (subscriptionIds.Count == 0)
        {
            return new Dictionary<string, WebhookDeliveryDocument>();
        }

        var deliveries = await db
            .Deliveries.Find(
                Builders<WebhookDeliveryDocument>.Filter.In(
                    item => item.SubscriptionId,
                    subscriptionIds
                ) & Builders<WebhookDeliveryDocument>.Filter.Ne(item => item.Status, "deduplicated")
            )
            .SortByDescending(item => item.DeliveredAt)
            .ToListAsync(cancellationToken);

        return deliveries
            .GroupBy(item => item.SubscriptionId)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public async Task<WebhookDeliveryDocument?> GetByIdAsync(
        string deliveryId,
        CancellationToken cancellationToken
    )
    {
        return await db
            .Deliveries.Find(delivery =>
                delivery.Id == deliveryId && delivery.Status != "deduplicated"
            )
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> EnqueueAsync(
        WebhookDeliveryDocument delivery,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await db
                .Deliveries.WithWriteConcern(WriteConcern.WMajority.With(journal: true))
                .InsertOneAsync(delivery, cancellationToken: cancellationToken);

            return true;
        }
        catch (MongoWriteException ex)
            when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await db
                .Deliveries.WithReadConcern(ReadConcern.Majority)
                .Find(x =>
                    (
                        x.IdempotencyKey == delivery.IdempotencyKey
                        || (delivery.Trigger == "automatic" && x.Trigger == "automatic")
                    )
                    && x.SubscriptionId == delivery.SubscriptionId
                    && x.EventId == delivery.EventId
                )
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is null)
                throw;

            return false;
        }
    }

    public async Task<long> CountOutstandingAsync(CancellationToken cancellationToken)
    {
        return await db.Deliveries.CountDocumentsAsync(
            (await NetworkFilterAsync(cancellationToken))
                & Builders<WebhookDeliveryDocument>.Filter.In(
                    item => item.Status,
                    ["pending", "retrying", "delivering", "waiting"]
                ),
            cancellationToken: cancellationToken
        );
    }

    private async Task<FilterDefinition<WebhookDeliveryDocument>> NetworkFilterAsync(
        CancellationToken ct
    )
    {
        var network = options?.Value.Network;

        if (network is null)
            return Builders<WebhookDeliveryDocument>.Filter.Empty;

        var ids = await db
            .Subscriptions.Find(x => x.Network == network)
            .Project(x => x.Id)
            .ToListAsync(ct);

        return Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.Network, network)
            | (
                Builders<WebhookDeliveryDocument>.Filter.In(x => x.SubscriptionId, ids)
                & (
                    Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.Network, "")
                    | Builders<WebhookDeliveryDocument>.Filter.Exists("network", false)
                )
            );
    }

    public async Task<WebhookDeliveryDocument?> ClaimDueAsync(CancellationToken cancellationToken)
    {
        FilterDefinition<WebhookDeliveryDocument> filter = new BsonDocument(
            "$or",
            new BsonArray
            {
                new BsonDocument
                {
                    {
                        "status",
                        new BsonDocument("$in", new BsonArray { "pending", "retrying", "waiting" })
                    },
                    {
                        "$expr",
                        new BsonDocument("$lte", new BsonArray { "$nextAttemptAt", "$$NOW" })
                    },
                },
                new BsonDocument
                {
                    { "status", "delivering" },
                    { "$expr", new BsonDocument("$lte", new BsonArray { "$leaseUntil", "$$NOW" }) },
                },
            }
        );
        filter &= await NetworkFilterAsync(cancellationToken);
        var owner = Guid.NewGuid().ToString("N");
        PipelineDefinition<WebhookDeliveryDocument, WebhookDeliveryDocument> pipeline = new[]
        {
            new BsonDocument(
                "$set",
                new BsonDocument
                {
                    { "status", "delivering" },
                    { "claimOwner", owner },
                    { "leaseUntil", new BsonDocument("$add", new BsonArray { "$$NOW", 120000 }) },
                }
            ),
        };

        return await db
            .Deliveries.WithWriteConcern(WriteConcern.WMajority.With(journal: true))
            .FindOneAndUpdateAsync(
                filter,
                Builders<WebhookDeliveryDocument>.Update.Pipeline(pipeline),
                new FindOneAndUpdateOptions<WebhookDeliveryDocument>
                {
                    ReturnDocument = ReturnDocument.After,
                    Sort = Builders<WebhookDeliveryDocument>.Sort.Ascending(x => x.NextAttemptAt),
                },
                cancellationToken
            );
    }

    private static FilterDefinition<WebhookDeliveryDocument> Owned(
        WebhookDeliveryDocument delivery
    ) =>
        Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.Id, delivery.Id)
        & Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.ClaimOwner, delivery.ClaimOwner)
        & Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.Status, "delivering")
        & new BsonDocument(
            "$expr",
            new BsonDocument("$gt", new BsonArray { "$leaseUntil", "$$NOW" })
        );

    public async Task<bool> SaveAllowanceAsync(
        WebhookDeliveryDocument delivery,
        CancellationToken cancellationToken
    )
    {
        var result = await db
            .Deliveries.WithWriteConcern(WriteConcern.WMajority.With(journal: true))
            .UpdateOneAsync(
                Owned(delivery),
                Builders<WebhookDeliveryDocument>
                    .Update.Set(x => x.AllowancePeriodId, delivery.AllowancePeriodId)
                    .Set(x => x.AllowanceCharged, delivery.AllowanceCharged),
                cancellationToken: cancellationToken
            );

        return result.MatchedCount == 1;
    }

    public async Task<bool> DeferAsync(
        WebhookDeliveryDocument delivery,
        string reason,
        CancellationToken cancellationToken
    )
    {
        var result = await db
            .Deliveries.WithWriteConcern(WriteConcern.WMajority.With(journal: true))
            .UpdateOneAsync(
                Owned(delivery),
                Builders<WebhookDeliveryDocument>
                    .Update.Set(x => x.Status, "waiting")
                    .Set(x => x.Error, reason)
                    .Set(x => x.NextAttemptAt, clock.GetUtcNow().UtcDateTime.AddSeconds(30))
                    .Unset(x => x.ClaimOwner)
                    .Unset(x => x.LeaseUntil),
                cancellationToken: cancellationToken
            );

        return result.MatchedCount == 1;
    }

    public async Task<bool> CompleteAsync(
        WebhookDeliveryDocument delivery,
        WebhookDeliveryAttemptDocument attempt,
        CancellationToken cancellationToken
    )
    {
        var updated = delivery with
        {
            ClaimOwner = null,
            LeaseUntil = null,
            AuditPending = true,
            Attempts = [.. delivery.Attempts, attempt],
        };
        var result = await db
            .Deliveries.WithWriteConcern(WriteConcern.WMajority.With(journal: true))
            .ReplaceOneAsync(Owned(delivery), updated, cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
            return false;

        await FlushAuditAsync(cancellationToken);

        return true;
    }

    public async Task FlushAuditAsync(CancellationToken cancellationToken)
    {
        var pending = await db
            .Deliveries.Find(x => x.AuditPending)
            .Limit(50)
            .ToListAsync(cancellationToken);

        foreach (var delivery in pending)
        {
            foreach (var attempt in delivery.Attempts)
                await db
                    .DeliveryAttempts.WithWriteConcern(WriteConcern.WMajority.With(journal: true))
                    .ReplaceOneAsync(
                        x => x.Id == attempt.Id,
                        attempt,
                        new ReplaceOptions { IsUpsert = true },
                        cancellationToken
                    );

            await db.Deliveries.UpdateOneAsync(
                x => x.Id == delivery.Id && x.AttemptCount == delivery.AttemptCount,
                Builders<WebhookDeliveryDocument>.Update.Set(x => x.AuditPending, false),
                cancellationToken: cancellationToken
            );
        }
    }

    public async Task PurgeExpiredAsync(
        DateTime payloadCutoff,
        DateTime historyCutoff,
        CancellationToken cancellationToken
    )
    {
        await db.Deliveries.UpdateManyAsync(
            Builders<WebhookDeliveryDocument>.Filter.And(
                Builders<WebhookDeliveryDocument>.Filter.Lt(
                    item => item.DeliveredAt,
                    payloadCutoff
                ),
                Builders<WebhookDeliveryDocument>.Filter.Ne(item => item.PayloadJson, null),
                Builders<WebhookDeliveryDocument>.Filter.In(
                    item => item.Status,
                    ["succeeded", "dead_letter", "cancelled"]
                )
            ),
            Builders<WebhookDeliveryDocument>.Update.Unset(item => item.PayloadJson),
            cancellationToken: cancellationToken
        );

        var expired = await db
            .Deliveries.Find(
                Builders<WebhookDeliveryDocument>.Filter.And(
                    Builders<WebhookDeliveryDocument>.Filter.Lt(
                        item => item.DeliveredAt,
                        historyCutoff
                    ),
                    Builders<WebhookDeliveryDocument>.Filter.In(
                        item => item.Status,
                        ["succeeded", "dead_letter", "cancelled"]
                    )
                )
            )
            .Project(item => item.Id)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
            return;

        await db.DeliveryAttempts.DeleteManyAsync(
            item => expired.Contains(item.DeliveryId),
            cancellationToken
        );

        await db.Deliveries.UpdateManyAsync(
            item => expired.Contains(item.Id) && item.Trigger == "automatic",
            Builders<WebhookDeliveryDocument>
                .Update.Set(item => item.Status, "deduplicated")
                .Unset(item => item.PayloadJson)
                .Unset(item => item.Error)
                .Unset(item => item.WebhookUrl)
                .Unset(item => item.NextAttemptAt)
                .Unset(item => item.LeaseUntil)
                .Unset(item => item.ClaimOwner)
                .Unset(item => item.Attempts)
                .Unset(item => item.AuditPending),
            cancellationToken: cancellationToken
        );
        await db.Deliveries.DeleteManyAsync(
            item => expired.Contains(item.Id) && item.Trigger != "automatic",
            cancellationToken
        );
    }

    public async Task DeleteBySubscriptionIdsAsync(
        IReadOnlyList<string> subscriptionIds,
        CancellationToken cancellationToken
    )
    {
        if (subscriptionIds.Count == 0)
            return;

        await db
            .ReplayJobs.WithWriteConcern(WriteConcern.WMajority.With(journal: true))
            .UpdateManyAsync(
                x => subscriptionIds.Contains(x.SubscriptionId) && x.Active,
                Builders<ReplayJobDocument>
                    .Update.Set(x => x.Status, "cancelled")
                    .Set(x => x.Active, false)
                    .Set(x => x.UpdatedAt, clock.GetUtcNow().UtcDateTime)
                    .Unset(x => x.Owner)
                    .Unset(x => x.LeaseUntil),
                cancellationToken: cancellationToken
            );
        var deliveryIds = await db
            .Deliveries.Find(item => subscriptionIds.Contains(item.SubscriptionId))
            .Project(item => item.Id)
            .ToListAsync(cancellationToken);

        if (deliveryIds.Count > 0)
            await db.DeliveryAttempts.DeleteManyAsync(
                item => deliveryIds.Contains(item.DeliveryId),
                cancellationToken
            );

        await db.Deliveries.UpdateManyAsync(
            item => subscriptionIds.Contains(item.SubscriptionId) && item.Trigger == "automatic",
            Builders<WebhookDeliveryDocument>
                .Update.Set(x => x.Status, "deduplicated")
                .Unset(x => x.PayloadJson)
                .Unset(x => x.Attempts)
                .Unset(x => x.ClaimOwner)
                .Unset(x => x.LeaseUntil)
                .Unset(x => x.WebhookUrl)
                .Unset(x => x.Error)
                .Unset(x => x.NextAttemptAt)
                .Set(x => x.AuditPending, false),
            cancellationToken: cancellationToken
        );
        await db.Deliveries.DeleteManyAsync(
            item => subscriptionIds.Contains(item.SubscriptionId) && item.Trigger != "automatic",
            cancellationToken
        );
    }
}
