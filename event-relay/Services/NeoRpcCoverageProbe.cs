using System.Diagnostics;
using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public sealed record RpcBlockEvidence(
    uint Height,
    string Hash,
    int Transactions,
    int LogsChecked,
    int Events,
    bool Complete
);

public sealed record RpcProbeEvidence(
    string Endpoint,
    string Status,
    uint? StartTip,
    uint? EndTip,
    double Seconds,
    double? BlocksPerSecond,
    double? LogsPerSecond,
    double? ChainBlocksPerSecond,
    IReadOnlyList<RpcBlockEvidence> Blocks,
    string? FailureKind
);

public static class NeoRpcCoverageProbe
{
    public static async Task<RpcProbeEvidence> RunAsync(
        INeoRpcClient rpc,
        string endpoint,
        string network,
        uint from,
        uint to,
        int maxLogsPerBlock,
        CancellationToken ct
    )
    {
        if (
            to < from
            || (ulong)to - from + 1 > 100
            || to == uint.MaxValue
            || maxLogsPerBlock is < 1 or > 1000
        )
            throw new ArgumentException("Probe requires 1–100 blocks and 1–1000 logs per block.");

        var watch = Stopwatch.StartNew();
        var blocks = new List<RpcBlockEvidence>();
        uint? start = null,
            end = null;

        try
        {
            await rpc.ValidateNetworkAsync(ct);
            var count = await rpc.GetBlockCountAsync(ct);
            start = count - 1;

            if (to >= count)
                throw new ArgumentException("Probe range exceeds the chain tip.");

            for (var height = from; height <= to; height++)
            {
                var block = await rpc.GetBlockAsync(height, ct);
                var logs = 0;
                var events = 0;

                foreach (var tx in block.TransactionHashes.Take(maxLogsPerBlock))
                {
                    var log = await rpc.GetApplicationLogAsync(tx, ct);
                    events += NeoApplicationLogParser.Extract(log, network, tx, block).Count;
                    logs++;
                }

                blocks.Add(
                    new(
                        height,
                        block.Hash,
                        block.TransactionHashes.Count,
                        logs,
                        events,
                        logs == block.TransactionHashes.Count
                    )
                );
            }

            end = (await rpc.GetBlockCountAsync(ct)) - 1;
            var checkedLogs = blocks.Sum(x => x.LogsChecked);

            return new(
                RelayHealthService.PublicEndpoint(endpoint),
                checkedLogs == 0 ? "inconclusive_empty_blocks"
                    : blocks.All(x => x.Complete) ? "passed_sample"
                    : "incomplete_log_sample",
                start,
                end,
                watch.Elapsed.TotalSeconds,
                blocks.Count / watch.Elapsed.TotalSeconds,
                checkedLogs / watch.Elapsed.TotalSeconds,
                Math.Max(0, (long)end.Value - start.Value) / watch.Elapsed.TotalSeconds,
                blocks,
                null
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(
                RelayHealthService.PublicEndpoint(endpoint),
                "blocked",
                start,
                end,
                watch.Elapsed.TotalSeconds,
                null,
                null,
                null,
                blocks,
                ex is NeoRpcException rpcError ? rpcError.Kind.ToString() : ex.GetType().Name
            );
        }
    }
}
