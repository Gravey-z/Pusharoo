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
        if (existing is not null) return existing;
        var free = new RelayEntitlementDocument { Id = $"{projectId}:{network}", ProjectId = projectId, Network = network, PeriodStart = now, PeriodEndsAt = now.AddDays(settings.FreeBetaPeriodDays), MaxActiveSubscriptions = network == "neo3:testnet" ? settings.FreeTestnetMaxActiveSubscriptions : 0, MaxEvents = network == "neo3:testnet" ? settings.FreeTestnetMaxEvents : 0, Status = network == "neo3:testnet" ? "active" : "expired" };
        try { await db.Entitlements.InsertOneAsync(free, cancellationToken: ct); return free; }
        catch (MongoWriteException) { return (await db.Entitlements.Find(x => x.ProjectId == projectId && x.Network == network).FirstAsync(ct)); }
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
}
