using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
namespace Pusharoo.EventRelay.Services;

public sealed class NeoBlockScanner(INeoRpcClient rpc, IEventCheckpointRepository checkpoints,
    IWebhookSubscriptionRepository subscriptions, IEventDeliveryQueue deliveries, RelayOperationsService operations,
    IOptions<NeoRpcOptions> options, ILogger<NeoBlockScanner> logger)
{
    private readonly NeoRpcOptions settings = options.Value;

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var id = $"neo:{settings.Network}";
        var owner = Guid.NewGuid().ToString("N");
        var duration = TimeSpan.FromSeconds(settings.ScannerLeaseSeconds);
        var checkpoint = await checkpoints.TryAcquireAsync(id, owner, duration, cancellationToken);
        if (checkpoint is null) return; // Another replica owns the network.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? leaseFailure = null;
        async Task RenewLeaseAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(duration / 3, work.Token);
                    if (!await checkpoints.RenewAsync(id, owner, duration, work.Token))
                        throw new InvalidOperationException("Scanner checkpoint lease was lost.");
                }
            }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { }
            catch (Exception ex) { leaseFailure = ex; await work.CancelAsync(); }
        }
        var renewal = RenewLeaseAsync();
        uint? block = checkpoint.NextBlock;
        string? transaction = null;
        try
        {
            if (checkpoint.BlockingError is not null)
            {
                operations.RecordScannerFailure(checkpoint.BlockingError);
                logger.LogError("Scanner is blocked at {BlockIndex}, transaction {TransactionHash}, endpoint {Endpoint}: {Message}. Explicit operator recovery is required.",
                    checkpoint.BlockingError.BlockIndex, checkpoint.BlockingError.TransactionHash, checkpoint.BlockingError.Endpoint, checkpoint.BlockingError.Message);
                return;
            }
            await rpc.ValidateNetworkAsync(work.Token);
            var count = await rpc.GetBlockCountAsync(work.Token);
            if (count == 0) throw new FormatException("RPC returned an empty chain.");
            var tip = count - 1;
            if (tip < settings.ConfirmationBlocks) return;
            var confirmedTip = tip - settings.ConfirmationBlocks;
            operations.RecordChainTip(tip, confirmedTip);
            if (!await checkpoints.RecordTipAsync(id, owner, tip, confirmedTip, work.Token))
                throw new InvalidOperationException("Chain observation lost checkpoint ownership.");
            if (checkpoint.NextBlock is null)
            {
                // Persist the initial boundary before queuing anything so restart cannot choose a later tip.
                var initial = settings.StartAtLatest ? checked(confirmedTip + 1) : settings.StartBlock ?? confirmedTip;
                if (!await checkpoints.AdvanceAsync(id, owner, null, initial, work.Token))
                    throw new InvalidOperationException("Checkpoint initialization lost ownership.");
                block = initial;
            }
            var next = block!.Value;
            if (next > confirmedTip)
            {
                operations.RecordScannerProgress(confirmedTip, next == 0 ? 0 : next - 1);
                operations.ClearScannerFailure();
                operations.ScannerHeartbeat();
                return;
            }
            var end = (uint)Math.Min(confirmedTip, (ulong)next + (uint)settings.MaxBlocksPerPoll - 1);
            for (var index = next; index <= end; index++)
            {
                block = index;
                transaction = null;
                var timing = System.Diagnostics.Stopwatch.StartNew();
                long events = 0;
                var confirmedBlock = await rpc.GetBlockAsync(index, work.Token);
                foreach (var hash in confirmedBlock.TransactionHashes)
                {
                    transaction = hash;
                    var log = await rpc.GetApplicationLogAsync(hash, work.Token);
                    // Parse the entire transaction before enqueuing any of its notifications.
                    foreach (var observed in NeoApplicationLogParser.Extract(log, settings.Network, hash, confirmedBlock))
                    {
                        events++;
                        var matches = await subscriptions.GetMatchingAsync(observed.Network, observed.ContractHash, observed.EventName, work.Token);
                        foreach (var subscription in matches)
                        {
                            work.Token.ThrowIfCancellationRequested();
                            await deliveries.QueueAsync(subscription, observed, work.Token);
                        }
                    }
                }
                transaction = null;
                work.Token.ThrowIfCancellationRequested();
                if (!await checkpoints.AdvanceAsync(id, owner, index, checked(index + 1), work.Token))
                    throw new InvalidOperationException("Checkpoint advancement lost ownership or its expected height changed.");
                operations.RecordScannedBlock(confirmedBlock.TransactionHashes.Count, events, timing.ElapsedMilliseconds);
                operations.RecordScannerProgress(confirmedTip, index);
                operations.ClearScannerFailure();
                operations.ScannerHeartbeat();
                logger.LogInformation("Neo scanner completed {Network} block {BlockIndex}; checkpoint is {NextBlock}.", settings.Network, index, index + 1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var actual = leaseFailure ?? ex;
            var rpcError = actual as NeoRpcException;
            var error = new ScannerBlockingError(block, transaction, rpcError?.Endpoint ?? settings.Endpoint,
                rpcError?.Kind.ToString() ?? (actual is FormatException ? "InvalidResponse" : "ScannerFailure"), actual.Message, DateTime.UtcNow);
            operations.RecordScannerFailure(error);
            // Provider-declared missing history, corrupt results and mismatched networks must never become a silent skip.
            if (rpcError?.Kind is NeoRpcFailureKind.UnavailableHistory or NeoRpcFailureKind.InvalidResponse or NeoRpcFailureKind.NetworkMismatch
                || actual is FormatException)
            {
                using var persistTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (!await checkpoints.BlockAsync(id, owner, error, persistTimeout.Token))
                    logger.LogWarning("Could not persist scanner blocking error because checkpoint ownership was lost.");
            }
            logger.LogError(actual, "Scanner retained checkpoint for {Network} block {BlockIndex}, transaction {TransactionHash}, endpoint {Endpoint}, failure {Kind}.",
                settings.Network, block, transaction, error.Endpoint, error.Kind);
            if (leaseFailure is not null) throw new InvalidOperationException("Scanner work was cancelled after losing its checkpoint lease.", leaseFailure);
            throw;
        }
        finally
        {
            await work.CancelAsync();
            await renewal;
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await checkpoints.ReleaseAsync(id, owner, releaseTimeout.Token); }
            catch (Exception ex) { logger.LogWarning(ex, "Scanner lease release failed; it will expire automatically."); }
        }
    }
}
