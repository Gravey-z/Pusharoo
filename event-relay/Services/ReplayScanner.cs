using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class ReplayScanner(
    INeoRpcClient rpc,
    ReplayJobRepository jobs,
    ReplayAdmissionService admissions,
    IWebhookSubscriptionRepository subscriptions,
    IEventDeliveryQueue queue,
    IWebhookDeliveryRepository deliveries,
    IOptions<NeoRpcOptions> rpcOptions,
    IOptions<EventRelayOptions> options,
    ILogger<ReplayScanner> logger,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        if (await deliveries.CountOutstandingAsync(ct) >= options.Value.MaxQueueDepth)
            return false;

        var job = await admissions.ClaimAsync(rpcOptions.Value.Network, ct);

        if (job is null)
            return false;

        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Renew()
        {
            try
            {
                while (!work.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), clock, work.Token);

                    if (!await jobs.RenewAsync(job, work.Token))
                    {
                        await work.CancelAsync();

                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Replay lease renewal failed.");
                await work.CancelAsync();
            }
        }

        var renewal = Renew();
        uint height = job.NextBlock;
        string? transaction = null;

        try
        {
            var targets = job.Targets ?? [new ReplayTarget(job.SubscriptionId, job.EventName)];
            var recipients = new List<WebhookSubscriptionDocument>();

            foreach (var target in targets)
            {
                var sub = await subscriptions.GetByIdAsync(target.SubscriptionId, work.Token);

                if (sub is null || sub.ProjectId != job.ProjectId || sub.Network != job.Network)
                    throw new FormatException(
                        "Replay webhook no longer exists on this project/network."
                    );

                if (sub.ContractHash != job.ContractHash || sub.EventName != target.EventName)
                    throw new FormatException(
                        "Webhook filter changed; cancel this job and create a new range."
                    );

                if (!sub.IsEnabled)
                {
                    await jobs.ActionAsync(job.Id, "pause", work.Token);

                    return true;
                }

                recipients.Add(sub);
            }

            await rpc.ValidateNetworkAsync(work.Token);
            var count = await rpc.GetBlockCountAsync(work.Token);

            if (
                count == 0
                || count - 1 < rpcOptions.Value.ConfirmationBlocks
                || job.ToBlock > count - 1 - rpcOptions.Value.ConfirmationBlocks
            )
                throw new FormatException("Replay range is no longer confirmed on this network.");

            var end = (uint)
                Math.Min(
                    job.ToBlock,
                    (ulong)height + (uint)options.Value.ReplayMaxBlocksPerPoll - 1
                );

            for (; height <= end; height++)
            {
                transaction = null;

                if (!await jobs.RenewAsync(job, work.Token))
                    return true;

                var block = await rpc.GetBlockAsync(height, work.Token);

                foreach (var tx in block.TransactionHashes)
                {
                    transaction = tx;
                    var log = await rpc.GetApplicationLogAsync(tx, work.Token);

                    foreach (
                        var observed in NeoApplicationLogParser.Extract(log, job.Network, tx, block)
                    )
                        if (
                            observed.ContractHash == job.ContractHash
                            && (
                                job.SelectedEventNames is null
                                    ? job.EventName is null || observed.EventName == job.EventName
                                    : job.SelectedEventNames.Contains(
                                        observed.EventName,
                                        StringComparer.Ordinal
                                    )
                            )
                        )
                        {
                            foreach (
                                var sub in recipients.Where(x =>
                                    x.EventName is null || x.EventName == observed.EventName
                                )
                            )
                            {
                                if (!await jobs.RenewAsync(job, work.Token))
                                    return true;

                                await queue.QueueAsync(sub, observed, work.Token);
                            }
                        }
                }

                if (!await jobs.AdvanceAsync(job, height, work.Token))
                    return true;

                if (
                    await deliveries.CountOutstandingAsync(work.Token)
                    >= options.Value.MaxQueueDepth
                )
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var rpcError = ex as NeoRpcException;
            var error = new ScannerBlockingError(
                height,
                transaction,
                rpcError?.Endpoint ?? rpcOptions.Value.Endpoint,
                rpcError?.Kind.ToString() ?? "ReplayFailure",
                ex.Message,
                clock.GetUtcNow().UtcDateTime
            );
            var blocked =
                ex is FormatException
                || rpcError?.Kind
                    is NeoRpcFailureKind.UnavailableHistory
                        or NeoRpcFailureKind.InvalidResponse
                        or NeoRpcFailureKind.NetworkMismatch;
            await jobs.FailAsync(job, error, blocked, ct);
            logger.LogError(
                ex,
                "Replay {JobId} retained block {BlockIndex}; blocked: {Blocked}.",
                job.Id,
                height,
                blocked
            );
        }
        finally
        {
            await work.CancelAsync();
            await renewal;
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);

            try
            {
                await jobs.ReleaseAsync(job, release.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Replay lease release failed; claim will expire.");
            }
        }

        return true;
    }
}
