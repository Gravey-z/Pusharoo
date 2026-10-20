using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed partial class RelayLifecycleService
{
    // Publishing accepted financial terms uses the same fence as admission and
    // retention. A stale free-trial sweep cannot delete a paid subscription.
    public async Task PublishPeriodAsync(RelayBillingDecisionDocument decision, RelayGrantAllocation allocation, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = await EnsureAsync(decision.ProjectId, ct);
            var publication = new RelayPeriodPublication($"{decision.Id}:{allocation.Id}", decision, allocation);
            var filter = Builders<RelayProjectAdmissionDocument>.Filter.Where(x => x.Id == state.Id && x.Version == state.Version
                && x.Mutation == null && x.Transition == null && x.Publication == null);
            if ((await db.ProjectAdmissions.UpdateOneAsync(filter,
                Builders<RelayProjectAdmissionDocument>.Update.Set(x => x.Publication, publication).Inc(x => x.Version, 1), cancellationToken: ct)).ModifiedCount == 0) continue;
            await ProjectPublicationAsync(state with { Publication = publication }, ct);
            await EnsureAsync(decision.ProjectId, ct);
            return;
        }
        throw new IOException("Period publication changed concurrently; the accepted payment remains recoverable.");
    }

    private async Task ProjectPublicationAsync(RelayProjectAdmissionDocument state, CancellationToken ct)
    {
        var publication = state.Publication!;
        // Only immutable insert/upgrade methods are used here; no catalog lookup
        // or readiness/manifest write can reprice the accepted decision.
        var store = new RelayPackageStore(db, new RelayPackageCatalog(Microsoft.Extensions.Options.Options.Create(new RelayPackageOptions())), options);
        await RelayPaymentApplicationService.PreparePeriodAsync(db, store, publication.Decision, publication.Allocation, ct);
        await db.ProjectAdmissions.UpdateOneAsync(x => x.Id == state.Id && x.Publication != null && x.Publication.Id == publication.Id,
            Builders<RelayProjectAdmissionDocument>.Update.Set(x => x.Publication, null), cancellationToken: ct);
    }
}
