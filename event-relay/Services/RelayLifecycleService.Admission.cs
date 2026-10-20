using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed partial class RelayLifecycleService
{
    private FilterDefinition<RelayProjectAdmissionDocument> WithinBoundary() =>
        new BsonDocument("$or", new BsonArray { new BsonDocument("validUntil", BsonNull.Value),
            new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$validUntil",
                ReferenceEquals(clock, TimeProvider.System) ? (BsonValue)"$$NOW" : Now })) });

    public async Task<bool> WasChargedAsync(string periodId, string deliveryId, CancellationToken ct)
    {
        if (await db.ProjectAdmissions.Find(x => x.Mainnet != null && x.Mainnet.PeriodId == periodId && x.Mainnet.Receipts.Contains(deliveryId)
            || x.Testnet != null && x.Testnet.PeriodId == periodId && x.Testnet.Receipts.Contains(deliveryId)).AnyAsync(ct)) return true;
        return await db.AllowancePeriods.WithReadConcern(ReadConcern.Majority)
            .Find(x => x.Id == periodId && x.ChargedDeliveries.Contains(deliveryId)).AnyAsync(ct);
    }

    // The caller persists this selected period before calling. If the boundary
    // changed in between, reject it and let the next delivery pass select again.
    public async Task<bool> TryConsumeAsync(RelayEntitlementDocument expected, string deliveryId, CancellationToken ct, string? subscriptionId = null)
    {
        var state = await EnsureAsync(expected.ProjectId, ct);
        var budget = Budget(state, expected.Network)!;
        var periodId = RelayEntitlementService.PeriodId(expected);
        if (await WasChargedAsync(periodId, deliveryId, ct)) return true;
        if (budget.PeriodId != periodId || budget.Entitlement.Status != "active"
            || budget.Entitlement.PeriodStart > Now || budget.Entitlement.PeriodEndsAt <= Now) return false;
        var field = Field(expected.Network);
        var now = Now;
        var filter = new BsonDocument {
            { "_id", expected.ProjectId }, { "version", state.Version }, { "transition", BsonNull.Value }, { "mutation", BsonNull.Value }, { "publication", BsonNull.Value },
            { field + ".periodId", periodId }, { field + ".entitlement.status", "active" },
            { field + ".receipts", new BsonDocument("$ne", deliveryId) },
            { "$expr", new BsonDocument("$and", new BsonArray {
                new BsonDocument("$lt", new BsonArray { "$" + field + ".eventsUsed", "$" + field + ".entitlement.maxEvents" }),
                new BsonDocument("$lte", new BsonArray { "$" + field + ".entitlement.periodStart", now }),
                new BsonDocument("$gt", new BsonArray { "$" + field + ".entitlement.periodEndsAt", now }) }) }
        };
        if (subscriptionId is not null) filter.Add("slots", new BsonDocument("$elemMatch", new BsonDocument {
            { "_id", subscriptionId }, { "Network", expected.Network } }));
        // Production uses the MongoDB clock as a second boundary guard; injected
        // clocks are used consistently by deterministic boundary fixtures.
        if (ReferenceEquals(clock, TimeProvider.System))
            filter.Add("$and", new BsonArray { new BsonDocument("$expr", new BsonDocument("$gt",
                new BsonArray { "$" + field + ".entitlement.periodEndsAt", "$$NOW" })) });
        var updated = await db.ProjectAdmissions.FindOneAndUpdateAsync(Builders<RelayProjectAdmissionDocument>.Filter.And(filter, WithinBoundary()),
            new BsonDocument { { "$inc", new BsonDocument(field + ".eventsUsed", 1) },
                { "$addToSet", new BsonDocument(field + ".receipts", deliveryId) } },
            new FindOneAndUpdateOptions<RelayProjectAdmissionDocument> { ReturnDocument = ReturnDocument.After,
                Projection = Builders<RelayProjectAdmissionDocument>.Projection.Include(field + ".eventsUsed") }, ct);
        if (updated is null) return await WasChargedAsync(periodId, deliveryId, ct);
        // Mirror for older readers/inventory. Authority survives a failure here;
        // recovery reads the central receipt and period retirement archives all.
        await ArchiveAsync(budget with { EventsUsed = Budget(updated, expected.Network)!.EventsUsed, Receipts = [deliveryId] }, false, ct);
        return true;
    }

    public async Task<WebhookSubscriptionDocument?> EffectiveSubscriptionAsync(WebhookSubscriptionDocument subscription, CancellationToken ct)
    {
        if (subscription.Deleted) return null;
        if (subscription.ProjectId is null) return subscription;
        var state = await EnsureAsync(subscription.ProjectId, ct);
        // Re-read after recovery: an old worker's configuration must not bypass a pause/delete.
        var current = await db.Subscriptions.Find(x => x.Id == subscription.Id && !x.Deleted).FirstOrDefaultAsync(ct);
        if (current is null) return null;
        var enabled = state.Slots.Any(x => x.Id == current.Id && x.Network == current.Network);
        return current with { IsEnabled = enabled, PauseReason = enabled ? null : current.PauseReason };
    }

    public async Task<string?> MutateSubscriptionAsync(WebhookSubscriptionDocument desired, bool create, bool delete, CancellationToken ct, bool expiredOnly = false)
    {
        var projectId = desired.ProjectId ?? throw new ArgumentException("A project is required.");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = await EnsureAsync(projectId, ct);
            var existing = await db.Subscriptions.Find(x => x.Id == desired.Id).FirstOrDefaultAsync(ct);
            if (existing is not null && existing.ProjectId != projectId) return "The webhook does not belong to this project.";
            if (create ? existing is not null : existing is null || existing.Deleted) return "The webhook changed; refresh and retry.";
            if (!create && existing!.AdmissionRevision != desired.AdmissionRevision) return "The webhook changed; refresh and retry.";
            var slots = state.Slots.Where(x => x.Id != desired.Id).ToList();
            var budget = Budget(state, desired.Network)!;
            if (expiredOnly && (budget.Entitlement.Plan == "paid" || existing?.ExpiresAt is null || existing.ExpiresAt > Now))
                return "The subscription is no longer an expired free-trial subscription.";
            var alreadyEnabled = state.Slots.Any(x => x.Id == desired.Id && x.Network == desired.Network);
            if (!delete && desired.IsEnabled)
            {
                if (!alreadyEnabled && (budget.Entitlement.Status != "active" || budget.Entitlement.PeriodStart > Now || budget.Entitlement.PeriodEndsAt <= Now))
                    return "Relay access is not active for this network.";
                var used = slots.Count(x => budget.Entitlement.Plan == "paid" || x.Network == desired.Network);
                if (used >= budget.Entitlement.MaxActiveSubscriptions) return "The Relay active-webhook limit has been reached.";
                slots.Add(new(desired.Id, desired.Network, desired.CreatedAt));
            }
            var mutation = new RelaySubscriptionMutation { Id = Guid.NewGuid().ToString("N"), Revision = state.Version + 1,
                Subscription = desired with { AdmissionRevision = state.Version + 1, PauseReason = desired.IsEnabled ? null : desired.PauseReason,
                    ExpiresAt = budget.Entitlement.Plan == "paid" ? null : desired.ExpiresAt }, Delete = delete };
            FilterDefinition<RelayProjectAdmissionDocument> admissionFilter = Builders<RelayProjectAdmissionDocument>.Filter.Where(
                x => x.Id == projectId && x.Version == state.Version && x.Transition == null && x.Mutation == null && x.Publication == null);
            admissionFilter &= WithinBoundary();
            if (!delete && desired.IsEnabled && !alreadyEnabled)
                admissionFilter &= new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray {
                    "$" + Field(desired.Network) + ".entitlement.periodEndsAt",
                    ReferenceEquals(clock, TimeProvider.System) ? (BsonValue)"$$NOW" : Now }));
            var result = await db.ProjectAdmissions.UpdateOneAsync(admissionFilter,
                Builders<RelayProjectAdmissionDocument>.Update.Set(x => x.Slots, slots.ToArray()).Set(x => x.Mutation, mutation)
                    .Inc(x => x.Version, 1), cancellationToken: ct);
            if (result.ModifiedCount == 0) continue;
            await ProjectMutationAsync(state with { Mutation = mutation }, ct);
            return null;
        }
        return "Relay admission changed concurrently; retry.";
    }

    public async Task<bool> AdmitAttemptAsync(string projectId, string subscriptionId, string network, CancellationToken ct)
    {
        var state = await EnsureAsync(projectId, ct);
        var filter = Builders<RelayProjectAdmissionDocument>.Filter.Where(x => x.Id == projectId && x.Version == state.Version
            && x.Transition == null && x.Mutation == null && x.Publication == null && x.Slots.Any(s => s.Id == subscriptionId && s.Network == network));
        return (await db.ProjectAdmissions.UpdateOneAsync(filter & WithinBoundary(),
            Builders<RelayProjectAdmissionDocument>.Update.Inc(x => x.AttemptAdmissions, 1), cancellationToken: ct)).ModifiedCount == 1;
    }

    private async Task ProjectMutationAsync(RelayProjectAdmissionDocument state, CancellationToken ct)
    {
        var mutation = state.Mutation!;
        var target = mutation.Subscription with { AdmissionRevision = mutation.Revision };
        if (mutation.Delete)
            target = new() { Id = target.Id, ProjectId = target.ProjectId, Network = target.Network,
                IsEnabled = false, Deleted = true, AdmissionRevision = mutation.Revision,
                CreatedAt = target.CreatedAt, UpdatedAt = target.UpdatedAt };
        var filter = Builders<WebhookSubscriptionDocument>.Filter.Eq(x => x.Id, target.Id)
            & (Builders<WebhookSubscriptionDocument>.Filter.Lt(x => x.AdmissionRevision, mutation.Revision)
                | Builders<WebhookSubscriptionDocument>.Filter.Exists(x => x.AdmissionRevision, false));
        try { await db.Subscriptions.ReplaceOneAsync(filter, target, new ReplaceOptions { IsUpsert = true }, ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var stored = await db.Subscriptions.Find(x => x.Id == target.Id).FirstOrDefaultAsync(ct);
            if (stored is null || stored.ProjectId != target.ProjectId || stored.AdmissionRevision < mutation.Revision) throw;
        }
        if (mutation.Delete) await new WebhookDeliveryRepository(db).DeleteBySubscriptionIdsAsync([target.Id], ct);
        await db.ProjectAdmissions.UpdateOneAsync(x => x.Id == state.Id && x.Mutation != null && x.Mutation.Id == mutation.Id,
            Builders<RelayProjectAdmissionDocument>.Update.Set(x => x.Mutation, null), cancellationToken: ct);
    }

    public async Task<RelayTransitionResponse> TransitionsAsync(string projectId, CancellationToken ct)
    {
        var state = await EnsureAsync(projectId, ct);
        return new(state.Selections, state.LastTransition);
    }

    public async Task SetDowngradeSelectionAsync(string projectId, SetRelayDowngradeSelectionRequest request, CancellationToken ct)
    {
        if (request.SubscriptionIds is null || request.SubscriptionIds.Distinct(StringComparer.Ordinal).Count() != request.SubscriptionIds.Length)
            throw new PaymentValidationException("Select distinct enabled webhooks.", 400);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = await EnsureAsync(projectId, ct);
            var period = await db.PurchasedPeriods.Find(x => x.Id == request.PeriodId && x.ProjectId == projectId).FirstOrDefaultAsync(ct);
            if (period is null || period.StartsAt <= Now || period.EffectiveTerms.PackageId != "starter")
                throw new PaymentValidationException("Select a future Starter boundary that has not been applied.", 409);
            var predecessor = await db.PurchasedPeriods.Find(x => x.ProjectId == projectId && x.EndsAt == period.StartsAt).FirstOrDefaultAsync(ct);
            if ((predecessor?.EffectiveTerms.MaxActiveSubscriptions ?? state.Mainnet?.Entitlement.MaxActiveSubscriptions ?? 0) <= period.EffectiveTerms.MaxActiveSubscriptions)
                throw new PaymentValidationException("This boundary does not lower the webhook limit.", 409);
            if (request.SubscriptionIds.Length > period.EffectiveTerms.MaxActiveSubscriptions
                || request.SubscriptionIds.Any(x => !state.Slots.Any(y => y.Id == x)))
                throw new PaymentValidationException("Select up to the boundary's limit from currently enabled project webhooks.", 400);
            var selections = state.Selections.Where(x => x.PeriodId != period.Id)
                .Append(new RelayDowngradeSelection(period.Id, request.SubscriptionIds, Now)).ToArray();
            var selectionFilter = Builders<RelayProjectAdmissionDocument>.Filter.Where(x => x.Id == projectId && x.Version == state.Version
                && x.Transition == null && x.Mutation == null && x.Publication == null) & WithinBoundary();
            if ((await db.ProjectAdmissions.UpdateOneAsync(selectionFilter,
                Builders<RelayProjectAdmissionDocument>.Update.Set(x => x.Selections, selections).Inc(x => x.Version, 1), cancellationToken: ct)).ModifiedCount == 1) return;
        }
        throw new PaymentValidationException("Boundary changed concurrently; refresh and retry.", 409);
    }

    public async Task ReconcileDueAsync(CancellationToken ct)
    {
        // Seed projects predating the admission collection, then serve due work
        // fairly; a failing project is deferred rather than starving the batch.
        var known = await db.Entitlements.Distinct(x => x.ProjectId, FilterDefinition<RelayEntitlementDocument>.Empty).ToListAsync(ct);
        foreach (var projectId in known)
            if (!await db.ProjectAdmissions.Find(x => x.Id == projectId).AnyAsync(ct)) await EnsureAsync(projectId, ct);
        var now = Now;
        var due = await db.ProjectAdmissions.Find(x => x.NextCheckAt <= now).SortBy(x => x.NextCheckAt).Limit(100).ToListAsync(ct);
        foreach (var state in due)
        {
            await db.ProjectAdmissions.UpdateOneAsync(x => x.Id == state.Id && x.NextCheckAt <= now,
                Builders<RelayProjectAdmissionDocument>.Update.Set(x => x.NextCheckAt, now.AddSeconds(5)), cancellationToken: ct);
            try { await EnsureAsync(state.Id, ct); }
            catch (Exception e) when (e is MongoException or IOException or RelayPaymentProjectionConflictException) { /* retained for the next bounded pass */ }
        }
    }
}
