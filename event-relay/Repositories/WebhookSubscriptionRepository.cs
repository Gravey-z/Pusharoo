using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public sealed class WebhookSubscriptionRepository(
    MongoDbContext db,
    TimeProvider? timeProvider = null,
    RelayLifecycleService? lifecycle = null
) : IWebhookSubscriptionRepository
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task InsertAsync(
        WebhookSubscriptionDocument subscription,
        CancellationToken cancellationToken
    )
    {
        if (lifecycle is not null && subscription.ProjectId is not null)
        {
            var error = await lifecycle.MutateSubscriptionAsync(subscription, true, false, cancellationToken);
            if (error is not null) throw new InvalidOperationException(error);
            return;
        }
        await db.Subscriptions.InsertOneAsync(subscription, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookSubscriptionDocument>> GetAllAsync(
        CancellationToken cancellationToken
    )
    {
        return await OverlayAsync(await db
            .Subscriptions.Find(x => !x.Deleted)
            .SortByDescending(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookSubscriptionDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var now = clock.GetUtcNow().UtcDateTime;

        return await OverlayAsync(await db
            .Subscriptions.Find(subscription =>
                subscription.ProjectId == projectId
                && !subscription.Deleted
                && (subscription.ExpiresAt == null || subscription.ExpiresAt > now)
            )
            .SortByDescending(subscription => subscription.CreatedAt)
            .ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<WebhookSubscriptionDocument?> GetByIdAsync(
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var value = await db.Subscriptions.Find(subscription => subscription.Id == subscriptionId && !subscription.Deleted)
            .FirstOrDefaultAsync(cancellationToken);
        return value is null ? null : (await OverlayAsync([value], cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<WebhookSubscriptionDocument>> GetMatchingAsync(
        string network,
        string contractHash,
        string eventName,
        CancellationToken cancellationToken
    )
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var filter = Builders<WebhookSubscriptionDocument>.Filter.And(
            Builders<WebhookSubscriptionDocument>.Filter.Ne(x => x.Deleted, true),
            Builders<WebhookSubscriptionDocument>.Filter.Eq(
                subscription => subscription.IsEnabled,
                true
            ),
            Builders<WebhookSubscriptionDocument>.Filter.Ne(
                subscription => subscription.ProjectId,
                null
            ),
            Builders<WebhookSubscriptionDocument>.Filter.Eq(
                subscription => subscription.Network,
                network
            ),
            Builders<WebhookSubscriptionDocument>.Filter.Eq(
                subscription => subscription.ContractHash,
                contractHash
            ),
            Builders<WebhookSubscriptionDocument>.Filter.Or(
                Builders<WebhookSubscriptionDocument>.Filter.Eq(
                    subscription => subscription.ExpiresAt,
                    null
                ),
                Builders<WebhookSubscriptionDocument>.Filter.Gt(
                    subscription => subscription.ExpiresAt,
                    now
                )
            ),
            Builders<WebhookSubscriptionDocument>.Filter.Or(
                Builders<WebhookSubscriptionDocument>.Filter.Eq(
                    subscription => subscription.EventName,
                    null
                ),
                Builders<WebhookSubscriptionDocument>.Filter.Eq(
                    subscription => subscription.EventName,
                    eventName
                )
            )
        );

        return (await OverlayAsync(await db.Subscriptions.Find(filter).ToListAsync(cancellationToken), cancellationToken))
            .Where(x => x.IsEnabled).ToArray();
    }

    public async Task<bool> ReplaceAsync(
        WebhookSubscriptionDocument subscription,
        CancellationToken cancellationToken
    )
    {
        if (lifecycle is not null && subscription.ProjectId is not null)
            return await lifecycle.MutateSubscriptionAsync(subscription, false, false, cancellationToken) is null;
        var result = await db.Subscriptions.ReplaceOneAsync(
            item => item.Id == subscription.Id,
            subscription,
            cancellationToken: cancellationToken
        );

        return result.ModifiedCount == 1;
    }

    public async Task<bool> DeleteAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        if (lifecycle is not null)
        {
            var subscription = await GetByIdAsync(subscriptionId, cancellationToken);
            return subscription?.ProjectId is not null
                && await lifecycle.MutateSubscriptionAsync(subscription, false, true, cancellationToken) is null;
        }
        var result = await db.Subscriptions.DeleteOneAsync(
            subscription => subscription.Id == subscriptionId,
            cancellationToken
        );

        return result.DeletedCount == 1;
    }

    public async Task SetFreeTestnetSubscriptionExpiryAsync(
        string projectId,
        int retentionDays,
        CancellationToken cancellationToken
    )
    {
        var filter = Builders<WebhookSubscriptionDocument>.Filter.And(
            Builders<WebhookSubscriptionDocument>.Filter.Eq(item => item.ProjectId, projectId),
            Builders<WebhookSubscriptionDocument>.Filter.Eq(item => item.Network, "neo3:testnet")
        );
        var subscriptions = await db.Subscriptions.Find(filter).ToListAsync(cancellationToken);
        var days = Math.Max(1, retentionDays);

        foreach (var subscription in subscriptions)
        {
            if (lifecycle is not null)
            {
                var state = await lifecycle.EnsureAsync(projectId, cancellationToken);
                if (state.Testnet?.Entitlement.Plan == "paid")
                {
                    await ClearTestnetSubscriptionExpiryAsync(projectId, cancellationToken);
                    return;
                }
                var current = await GetByIdAsync(subscription.Id, cancellationToken);
                if (current is not null)
                    await lifecycle.MutateSubscriptionAsync(current with { ExpiresAt = current.CreatedAt.AddDays(days) }, false, false, cancellationToken);
                continue;
            }
            await db.Subscriptions.UpdateOneAsync(
                item => item.Id == subscription.Id,
                Builders<WebhookSubscriptionDocument>.Update.Set(
                    item => item.ExpiresAt,
                    subscription.CreatedAt.AddDays(days)
                ),
                cancellationToken: cancellationToken
            );
        }
    }

    public async Task ClearTestnetSubscriptionExpiryAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var filter = Builders<WebhookSubscriptionDocument>.Filter.And(
            Builders<WebhookSubscriptionDocument>.Filter.Eq(item => item.ProjectId, projectId),
            Builders<WebhookSubscriptionDocument>.Filter.Eq(item => item.Network, "neo3:testnet"),
            Builders<WebhookSubscriptionDocument>.Filter.Ne(item => item.ExpiresAt, null)
        );
        await db.Subscriptions.UpdateManyAsync(
            filter,
            Builders<WebhookSubscriptionDocument>.Update.Set(item => item.ExpiresAt, null),
            cancellationToken: cancellationToken
        );
    }

    public async Task<IReadOnlyList<string>> DeleteExpiredAsync(
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        var filter = Builders<WebhookSubscriptionDocument>.Filter.And(
            Builders<WebhookSubscriptionDocument>.Filter.Eq(item => item.Network, "neo3:testnet"),
            Builders<WebhookSubscriptionDocument>.Filter.Lte(item => item.ExpiresAt, now)
        );
        var ids = await db
            .Subscriptions.Find(filter)
            .Project(item => item.Id)
            .ToListAsync(cancellationToken);

        if (lifecycle is not null)
        {
            var removed = new List<string>();
            foreach (var id in ids)
            {
                var value = await GetByIdAsync(id, cancellationToken);
                if (value?.ProjectId is null) continue;
                await lifecycle.EnsureAsync(value.ProjectId, cancellationToken);
                value = await GetByIdAsync(id, cancellationToken);
                if (value?.ExpiresAt is { } expiry && expiry <= now
                    && await lifecycle.MutateSubscriptionAsync(value, false, true, cancellationToken, expiredOnly: true) is null) removed.Add(id);
            }
            return removed;
        }

        if (ids.Count > 0)
            await db.Subscriptions.DeleteManyAsync(filter, cancellationToken);

        return ids;
    }

    private async Task<IReadOnlyList<WebhookSubscriptionDocument>> OverlayAsync(IReadOnlyList<WebhookSubscriptionDocument> items, CancellationToken ct)
    {
        var projects = items.Where(x => x.ProjectId is not null).Select(x => x.ProjectId!).Distinct().ToArray();
        var states = await db.ProjectAdmissions.Find(x => projects.Contains(x.Id)).Project(x => new { x.Id, x.Slots, x.Transition }).ToListAsync(ct);
        return items.Select(x => {
            var state = states.SingleOrDefault(s => s.Id == x.ProjectId);
            if (state is null) return x;
            var slots = state.Transition?.Slots ?? state.Slots;
            var enabled = slots.Any(s => s.Id == x.Id && s.Network == x.Network);
            return x with { IsEnabled = enabled, PauseReason = !enabled && x.IsEnabled ? "package_limit" : x.PauseReason };
        }).ToArray();
    }
}
