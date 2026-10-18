using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class ReplayAdmissionService(
    ReplayAdmissionRepository reservations,
    ReplayJobRepository jobs,
    ReplayAdmissionPolicy policy,
    IOptions<EventRelayOptions> options
)
{
    public async Task<ReplayAdmissionResult> TryCreateAsync(
        ReplayJobDocument job,
        CancellationToken ct
    )
    {
        var settings = options.Value;

        if (policy.ValidateRange(job, settings) is { } invalid)
            return invalid;

        await reservations.EnsureAsync(job.Network, ct);
        await reservations.RecoverAsync(job.Network, ct);

        // A revision-fenced network ledger reserves both quota and capacity atomically,
        // even on standalone MongoDB. A lost race must re-read and re-evaluate policy.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var snapshot = await reservations.ReadAsync(job.Network, ct);
            var decision = policy.Evaluate(job, settings, snapshot.ServerNow, snapshot.Usage);

            if (!decision.Accepted)
                return decision;

            var reserved = await reservations.TryReserveAsync(job.Network, snapshot, job, ct);

            if (reserved == ReplayReservationStatus.Contended)
                continue;

            if (reserved == ReplayReservationStatus.HistoryFull)
                return new(
                    false,
                    429,
                    "The network recovery history is at capacity. Try again later as older reservations expire.",
                    snapshot.ServerNow.AddMinutes(1)
                );

            // The durable intent is committed before materialization. Interrupted inserts
            // are recovered before admission and before the replay worker claims jobs.
            await reservations.MaterializeAsync(job, ct);
            return new(true);
        }

        return new(false, 429, "Replay requests are busy. Try again shortly.");
    }

    public async Task<ReplayJobDocument?> ClaimAsync(string network, CancellationToken ct)
    {
        await reservations.RecoverAsync(network, ct);
        return await jobs.ClaimAsync(network, ct);
    }
}
