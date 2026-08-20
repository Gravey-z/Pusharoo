using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayEntitlementService(
    MongoDbContext db,
    IWebhookSubscriptionRepository subscriptions,
    IOptions<EventRelayOptions> options)
{
    private readonly EventRelayOptions settings = options.Value;
    public async Task<RelayEntitlementDocument> GetAsync(string projectId, string network, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await db.Entitlements.Find(x => x.ProjectId == projectId && x.Network == network).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            existing = await ApplyFreeTestnetAllowanceAsync(existing, now, ct);
            return await UpdateStatusAsync(existing, now, ct);
        }
        var free = new RelayEntitlementDocument { Id = $"{projectId}:{network}", ProjectId = projectId, Network = network, PeriodStart = now, PeriodEndsAt = now.AddDays(settings.FreeBetaPeriodDays), MaxActiveSubscriptions = network == "neo3:testnet" ? settings.FreeTestnetMaxActiveSubscriptions : 0, MaxEvents = network == "neo3:testnet" ? settings.FreeTestnetMaxEvents : 0, Status = network == "neo3:testnet" ? "active" : "expired", UpdatedAt = now };
        try { await db.Entitlements.InsertOneAsync(free, cancellationToken: ct); return free; }
        catch (MongoWriteException) { return (await db.Entitlements.Find(x => x.ProjectId == projectId && x.Network == network).FirstAsync(ct)); }
    }

    public async Task<IReadOnlyList<RelayEntitlementDocument>> GrantPaidAccessAsync(string projectId, string paymentId, CancellationToken ct)
    {
        return await Task.WhenAll(new[] { "neo3:mainnet", "neo3:testnet" }
            .Select(network => GrantPaidAccessForNetworkAsync(projectId, network, paymentId, ct)));
    }
    public async Task<bool> TryConsumeEventAsync(string projectId, string network, CancellationToken ct)
    {
        var entitlement = await GetAsync(projectId, network, ct);
        var now = DateTime.UtcNow;
        var filter = Builders<RelayEntitlementDocument>.Filter.And(
            Builders<RelayEntitlementDocument>.Filter.Eq(x => x.Id, entitlement.Id),
            Builders<RelayEntitlementDocument>.Filter.Eq(x => x.Status, "active"),
            Builders<RelayEntitlementDocument>.Filter.Gt(x => x.PeriodEndsAt, now),
            Builders<RelayEntitlementDocument>.Filter.Lt(x => x.EventsUsed, entitlement.MaxEvents));
        return await db.Entitlements.FindOneAndUpdateAsync(filter, Builders<RelayEntitlementDocument>.Update.Inc(x => x.EventsUsed, 1), new FindOneAndUpdateOptions<RelayEntitlementDocument> { ReturnDocument = ReturnDocument.After }, ct) is not null;
    }

    public async Task<IReadOnlyList<RelayEntitlementDocument>> ReconcileFreeTestnetTrialsAsync(CancellationToken ct)
    {
        var trials = await db.Entitlements
            .Find(item => item.Network == "neo3:testnet" && item.Plan == "free_beta")
            .Project(item => item.ProjectId)
            .ToListAsync(ct);
        var entitlements = await Task.WhenAll(
            trials.Distinct(StringComparer.Ordinal).Select(projectId => GetAsync(projectId, "neo3:testnet", ct)));
        return entitlements.Where(item => item.Plan == "free_beta").ToArray();
    }

    private async Task<RelayEntitlementDocument> UpdateStatusAsync(RelayEntitlementDocument entitlement, DateTime now, CancellationToken ct)
    {
        var status = entitlement.Status;
        if (entitlement.PeriodEndsAt <= now)
            status = entitlement.GraceEndsAt is { } graceEndsAt && graceEndsAt > now ? "grace" : "expired";
        if (status == entitlement.Status) return entitlement;
        var update = Builders<RelayEntitlementDocument>.Update.Set(x => x.Status, status).Set(x => x.UpdatedAt, now);
        await db.Entitlements.UpdateOneAsync(x => x.Id == entitlement.Id, update, cancellationToken: ct);
        return new RelayEntitlementDocument { Id = entitlement.Id, ProjectId = entitlement.ProjectId, Network = entitlement.Network, Plan = entitlement.Plan, Status = status, PeriodStart = entitlement.PeriodStart, PeriodEndsAt = entitlement.PeriodEndsAt, MaxActiveSubscriptions = entitlement.MaxActiveSubscriptions, MaxEvents = entitlement.MaxEvents, EventsUsed = entitlement.EventsUsed, PaidUntil = entitlement.PaidUntil, GraceEndsAt = entitlement.GraceEndsAt, LastPaymentId = entitlement.LastPaymentId, UpdatedAt = now, Version = entitlement.Version, PaymentGrants = entitlement.PaymentGrants };
    }

    private async Task<RelayEntitlementDocument> ApplyFreeTestnetAllowanceAsync(
        RelayEntitlementDocument entitlement,
        DateTime now,
        CancellationToken ct)
    {
        if (entitlement.Network != "neo3:testnet" || entitlement.Plan != "free_beta") return entitlement;
        var resetAllowance = entitlement.PeriodEndsAt <= now;
        var periodStart = resetAllowance ? now : entitlement.PeriodStart;
        var periodEndsAt = periodStart.AddDays(settings.FreeBetaPeriodDays);
        var eventsUsed = resetAllowance ? 0 : entitlement.EventsUsed;
        if (!resetAllowance
            && entitlement.Status == "active"
            && entitlement.PeriodEndsAt == periodEndsAt
            && entitlement.MaxActiveSubscriptions == settings.FreeTestnetMaxActiveSubscriptions
            && entitlement.MaxEvents == settings.FreeTestnetMaxEvents) return entitlement;
        await db.Entitlements.UpdateOneAsync(
            item => item.Id == entitlement.Id && item.Plan == "free_beta",
            Builders<RelayEntitlementDocument>.Update
                .Set(item => item.PeriodEndsAt, periodEndsAt)
                .Set(item => item.PeriodStart, periodStart)
                .Set(item => item.MaxActiveSubscriptions, settings.FreeTestnetMaxActiveSubscriptions)
                .Set(item => item.MaxEvents, settings.FreeTestnetMaxEvents)
                .Set(item => item.EventsUsed, eventsUsed)
                .Set(item => item.Status, "active")
                .Set(item => item.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);
        return new RelayEntitlementDocument { Id = entitlement.Id, ProjectId = entitlement.ProjectId, Network = entitlement.Network, Plan = entitlement.Plan, Status = "active", PeriodStart = periodStart, PeriodEndsAt = periodEndsAt, MaxActiveSubscriptions = settings.FreeTestnetMaxActiveSubscriptions, MaxEvents = settings.FreeTestnetMaxEvents, EventsUsed = eventsUsed, PaidUntil = entitlement.PaidUntil, GraceEndsAt = entitlement.GraceEndsAt, LastPaymentId = entitlement.LastPaymentId, UpdatedAt = DateTime.UtcNow, Version = entitlement.Version, PaymentGrants = entitlement.PaymentGrants };
    }

    private async Task<RelayEntitlementDocument> GrantPaidAccessForNetworkAsync(string projectId, string network, string paymentId, CancellationToken ct)
    {
        while (true)
        {
            var current = await GetAsync(projectId, network, ct);
            var existingGrant = current.PaymentGrants.FirstOrDefault(grant => string.Equals(grant.PaymentId, paymentId, StringComparison.Ordinal));
            if (existingGrant is not null)
            {
                if (network == "neo3:testnet") await subscriptions.ClearTestnetSubscriptionExpiryAsync(projectId, ct);
                await RecordHistoryAsync(projectId, network, paymentId, existingGrant, ct);
                return current;
            }

            var now = DateTime.UtcNow;
            var start = current.PaidUntil is { } paidUntil && paidUntil > now ? paidUntil : now;
            var grant = new RelayEntitlementPaymentGrant
            {
                PaymentId = paymentId,
                PeriodStart = start,
                PeriodEndsAt = start.AddDays(settings.PaidPlanDays),
                GraceEndsAt = start.AddDays(settings.PaidPlanDays + settings.PaidGraceDays)
            };
            var updated = new RelayEntitlementDocument
            {
                Id = current.Id, ProjectId = current.ProjectId, Network = current.Network, Plan = "paid", Status = "active",
                PeriodStart = grant.PeriodStart, PeriodEndsAt = grant.PeriodEndsAt, PaidUntil = grant.PeriodEndsAt, GraceEndsAt = grant.GraceEndsAt,
                MaxActiveSubscriptions = settings.PaidMaxActiveSubscriptions, MaxEvents = settings.PaidMaxEvents, EventsUsed = 0,
                LastPaymentId = paymentId, UpdatedAt = now, Version = current.Version + 1,
                PaymentGrants = [.. current.PaymentGrants, grant]
            };
            var versionFilter = current.Version == 0
                ? Builders<RelayEntitlementDocument>.Filter.Or(
                    Builders<RelayEntitlementDocument>.Filter.Eq(item => item.Version, 0),
                    Builders<RelayEntitlementDocument>.Filter.Exists("version", false))
                : Builders<RelayEntitlementDocument>.Filter.Eq(item => item.Version, current.Version);
            var result = await db.Entitlements.ReplaceOneAsync(
                Builders<RelayEntitlementDocument>.Filter.And(
                    Builders<RelayEntitlementDocument>.Filter.Eq(item => item.Id, current.Id),
                    versionFilter),
                updated,
                cancellationToken: ct);
            if (result.ModifiedCount != 1) continue;

            if (network == "neo3:testnet") await subscriptions.ClearTestnetSubscriptionExpiryAsync(projectId, ct);
            await RecordHistoryAsync(projectId, network, paymentId, grant, ct);
            return updated;
        }
    }

    private async Task RecordHistoryAsync(string projectId, string network, string paymentId, RelayEntitlementPaymentGrant grant, CancellationToken ct)
    {
        var filter = Builders<RelayEntitlementHistoryDocument>.Filter.And(
            Builders<RelayEntitlementHistoryDocument>.Filter.Eq(item => item.PaymentId, paymentId),
            Builders<RelayEntitlementHistoryDocument>.Filter.Eq(item => item.Network, network));
        var history = new RelayEntitlementHistoryDocument
        {
            Id = $"{paymentId}:{network}", PaymentId = paymentId, ProjectId = projectId, Network = network,
            PeriodStart = grant.PeriodStart, PeriodEndsAt = grant.PeriodEndsAt, GraceEndsAt = grant.GraceEndsAt, CreatedAt = DateTime.UtcNow
        };
        await db.EntitlementHistory.ReplaceOneAsync(filter, history, new ReplaceOptions { IsUpsert = true }, ct);
    }
}
