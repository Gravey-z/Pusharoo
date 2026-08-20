using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayEntitlementService(MongoDbContext db, IOptions<EventRelayOptions> options)
{
    private readonly EventRelayOptions settings = options.Value;
    public async Task<RelayEntitlementDocument> GetAsync(string projectId, string network, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await db.Entitlements.Find(x => x.ProjectId == projectId && x.Network == network).FirstOrDefaultAsync(ct);
        if (existing is not null) return await UpdateStatusAsync(existing, now, ct);
        var free = new RelayEntitlementDocument { Id = $"{projectId}:{network}", ProjectId = projectId, Network = network, PeriodStart = now, PeriodEndsAt = now.AddDays(settings.FreeBetaPeriodDays), MaxActiveSubscriptions = network == "neo3:testnet" ? settings.FreeTestnetMaxActiveSubscriptions : 0, MaxEvents = network == "neo3:testnet" ? settings.FreeTestnetMaxEvents : 0, Status = network == "neo3:testnet" ? "active" : "expired", UpdatedAt = now };
        try { await db.Entitlements.InsertOneAsync(free, cancellationToken: ct); return free; }
        catch (MongoWriteException) { return (await db.Entitlements.Find(x => x.ProjectId == projectId && x.Network == network).FirstAsync(ct)); }
    }

    public async Task<IReadOnlyList<RelayEntitlementDocument>> GrantPaidAccessAsync(string projectId, string paymentId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (await db.EntitlementHistory.Find(x => x.PaymentId == paymentId).AnyAsync(ct))
        {
            return await Task.WhenAll(new[] { "neo3:mainnet", "neo3:testnet" }.Select(network => GetAsync(projectId, network, ct)));
        }
        var updated = new List<RelayEntitlementDocument>();
        foreach (var network in new[] { "neo3:mainnet", "neo3:testnet" })
        {
            var current = await GetAsync(projectId, network, ct);
            if (string.Equals(current.LastPaymentId, paymentId, StringComparison.Ordinal)) { updated.Add(current); continue; }
            var start = current.PaidUntil is { } paidUntil && paidUntil > now ? paidUntil : now;
            var endsAt = start.AddDays(settings.PaidPlanDays);
            var entitlement = new RelayEntitlementDocument
            {
                Id = current.Id, ProjectId = current.ProjectId, Network = current.Network, Plan = "paid", Status = "active",
                PeriodStart = start, PeriodEndsAt = endsAt, PaidUntil = endsAt, GraceEndsAt = endsAt.AddDays(settings.PaidGraceDays),
                MaxActiveSubscriptions = settings.PaidMaxActiveSubscriptions, MaxEvents = settings.PaidMaxEvents, EventsUsed = 0,
                LastPaymentId = paymentId, UpdatedAt = now
            };
            await db.Entitlements.ReplaceOneAsync(x => x.Id == entitlement.Id, entitlement, cancellationToken: ct);
            try
            {
                await db.EntitlementHistory.InsertOneAsync(new RelayEntitlementHistoryDocument { Id = Guid.NewGuid().ToString("n"), PaymentId = paymentId, ProjectId = projectId, Network = network, PeriodStart = start, PeriodEndsAt = endsAt, GraceEndsAt = entitlement.GraceEndsAt!.Value, CreatedAt = now }, cancellationToken: ct);
            }
            catch (MongoWriteException) { }
            updated.Add(entitlement);
        }
        return updated;
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

    private async Task<RelayEntitlementDocument> UpdateStatusAsync(RelayEntitlementDocument entitlement, DateTime now, CancellationToken ct)
    {
        var status = entitlement.Status;
        if (entitlement.PeriodEndsAt <= now)
            status = entitlement.GraceEndsAt is { } graceEndsAt && graceEndsAt > now ? "grace" : "expired";
        if (status == entitlement.Status) return entitlement;
        var update = Builders<RelayEntitlementDocument>.Update.Set(x => x.Status, status).Set(x => x.UpdatedAt, now);
        await db.Entitlements.UpdateOneAsync(x => x.Id == entitlement.Id, update, cancellationToken: ct);
        return new RelayEntitlementDocument { Id = entitlement.Id, ProjectId = entitlement.ProjectId, Network = entitlement.Network, Plan = entitlement.Plan, Status = status, PeriodStart = entitlement.PeriodStart, PeriodEndsAt = entitlement.PeriodEndsAt, MaxActiveSubscriptions = entitlement.MaxActiveSubscriptions, MaxEvents = entitlement.MaxEvents, EventsUsed = entitlement.EventsUsed, PaidUntil = entitlement.PaidUntil, GraceEndsAt = entitlement.GraceEndsAt, LastPaymentId = entitlement.LastPaymentId, UpdatedAt = now };
    }
}
