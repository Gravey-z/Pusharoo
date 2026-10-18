using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class ReplayAdmissionPolicy
{
    public ReplayAdmissionResult? ValidateRange(
        ReplayJobDocument job,
        EventRelayOptions settings
    ) =>
        job.ToBlock < job.FromBlock || job.TotalBlocks > settings.ReplayMaxRangeBlocks
            ? new(false, 400, "Replay range exceeds the configured limit.")
            : null;

    // Admission uses MongoDB's reservation time across replicas. Supplying it explicitly
    // keeps cooldown and quota boundaries testable without replacing the server clock.
    public ReplayAdmissionResult Evaluate(
        ReplayJobDocument job,
        EventRelayOptions settings,
        DateTime now,
        IReadOnlyList<ReplayAdmissionUsage> entries
    )
    {
        var project = entries.Where(x => x.ProjectId == job.ProjectId).ToArray();

        if (project.Any(x => x.Active))
            return new(
                false,
                409,
                "This project already has an active replay on this network. Complete or cancel it before starting another."
            );

        var recent = project
            .Where(x => x.AdmittedAt > now.AddDays(-1))
            .OrderBy(x => x.AdmittedAt)
            .ToArray();

        if (recent.Length > 0)
        {
            var next = recent[^1].AdmittedAt.AddSeconds(settings.ReplayCooldownSeconds);

            if (next > now)
                return new(
                    false,
                    429,
                    $"Replay cooldown: try again after {next:yyyy-MM-dd HH:mm:ss} UTC.",
                    next
                );
        }

        var used = recent.Sum(x => x.Blocks);

        if (used + job.TotalBlocks > settings.ReplayProjectBlocksPerDay)
        {
            var retry = now.AddDays(1);

            foreach (var entry in recent)
            {
                used -= entry.Blocks;

                retry = entry.AdmittedAt.AddDays(1);

                if (used + job.TotalBlocks <= settings.ReplayProjectBlocksPerDay)
                    break;
            }

            return new(
                false,
                429,
                $"Project replay quota is {settings.ReplayProjectBlocksPerDay:N0} requested blocks per rolling 24 hours on this network. Try this range again after {retry:yyyy-MM-dd HH:mm:ss} UTC. Cancelling does not refund quota.",
                retry
            );
        }

        if (entries.Count(x => x.Active) >= settings.ReplayMaxActiveJobsPerNetwork)
            return new(
                false,
                429,
                "The network replay queue is full. Try again in a minute.",
                now.AddMinutes(1)
            );

        var daily = entries.Where(x => x.AdmittedAt > now.AddDays(-1)).ToArray();

        if (daily.Length >= settings.ReplayMaxJobsPerDayPerNetwork)
        {
            var retry = daily.Min(x => x.AdmittedAt).AddDays(1);

            return new(
                false,
                429,
                $"The network daily replay-job limit has been reached. Try again after {retry:yyyy-MM-dd HH:mm:ss} UTC.",
                retry
            );
        }

        return new(true);
    }
}
