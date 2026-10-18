using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayOperationsService(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private readonly object gate = new();

    private DateTime scannerHeartbeat,
        workerHeartbeat,
        rpcSuccessAt;

    private string activeRpcEndpoint = "";

    private uint? networkTip,
        confirmedBlock,
        processedBlock;

    private ScannerBlockingError? scannerError;

    private long succeeded,
        failed,
        retried,
        deadLetters,
        latencyTotalMilliseconds,
        latencySamples,
        rpcFailures;

    private long scannedBlocks,
        scannedTransactions,
        observedEvents,
        scanMilliseconds,
        rpcRequests;

    private DateTime? lastSuccessfulScan;

    public void RecordScannerFailure(ScannerBlockingError error)
    {
        lock (gate)
            scannerError = error;
    }

    public void ClearScannerFailure()
    {
        lock (gate)
            scannerError = null;
    }

    public void ScannerHeartbeat()
    {
        lock (gate)
            scannerHeartbeat = clock.GetUtcNow().UtcDateTime;
    }

    public void WorkerHeartbeat()
    {
        lock (gate)
            workerHeartbeat = clock.GetUtcNow().UtcDateTime;
    }

    public void RecordChainTip(uint tip, uint confirmed)
    {
        lock (gate)
        {
            networkTip = tip;
            confirmedBlock = confirmed;
        }
    }

    public void RecordScannerProgress(uint confirmed, uint processed)
    {
        lock (gate)
        {
            confirmedBlock = confirmed;
            processedBlock = processed;
        }
    }

    public void RecordScannedBlock(long transactions, long events, long milliseconds)
    {
        lock (gate)
        {
            scannedBlocks++;
            scannedTransactions += transactions;
            observedEvents += events;
            scanMilliseconds += milliseconds;
            lastSuccessfulScan = clock.GetUtcNow().UtcDateTime;
        }
    }

    public void RecordRpcRequest()
    {
        lock (gate)
            rpcRequests++;
    }

    public void RecordRpcSuccess(string endpoint)
    {
        lock (gate)
        {
            activeRpcEndpoint = endpoint;
            rpcSuccessAt = clock.GetUtcNow().UtcDateTime;
        }
    }

    public void RecordRpcFailure()
    {
        lock (gate)
            rpcFailures++;
    }

    public void RecordDelivery(bool success, bool retry, bool deadLetter, long latency)
    {
        lock (gate)
        {
            if (success)
                succeeded++;
            else
                failed++;

            if (retry)
                retried++;

            if (deadLetter)
                deadLetters++;

            if (latency >= 0)
            {
                latencyTotalMilliseconds += latency;
                latencySamples++;
            }
        }
    }

    public RelayOperationsSnapshot Snapshot()
    {
        lock (gate)
            return new(
                scannerHeartbeat,
                workerHeartbeat,
                rpcSuccessAt,
                activeRpcEndpoint,
                confirmedBlock,
                processedBlock,
                succeeded,
                failed,
                retried,
                deadLetters,
                latencyTotalMilliseconds,
                latencySamples,
                rpcFailures,
                scannerError,
                networkTip,
                lastSuccessfulScan,
                scannedBlocks,
                scannedTransactions,
                observedEvents,
                scanMilliseconds,
                rpcRequests
            );
    }
}

public sealed record RelayOperationsSnapshot(
    DateTime ScannerHeartbeat,
    DateTime WorkerHeartbeat,
    DateTime RpcSuccessAt,
    string ActiveRpcEndpoint,
    uint? ConfirmedBlock,
    uint? ProcessedBlock,
    long Succeeded,
    long Failed,
    long Retried,
    long DeadLetters,
    long LatencyTotalMilliseconds,
    long LatencySamples,
    long RpcFailures,
    ScannerBlockingError? ScannerError,
    uint? NetworkTip,
    DateTime? LastSuccessfulScanAt,
    long ScannedBlocks,
    long ScannedTransactions,
    long ObservedEvents,
    long ScanMilliseconds,
    long RpcRequests
)
{
    public uint? ScannerLagBlocks =>
        ConfirmedBlock is { } tip && ProcessedBlock is { } scanned
            ? tip >= scanned
                ? tip - scanned
                : 0
            : null;

    public double? AverageDeliveryLatencyMilliseconds =>
        LatencySamples > 0 ? (double)LatencyTotalMilliseconds / LatencySamples : null;

    public double? SuccessRate =>
        Succeeded + Failed > 0 ? (double)Succeeded / (Succeeded + Failed) : null;

    public double? ScanBlocksPerSecond =>
        ScanMilliseconds > 0 ? ScannedBlocks * 1000d / ScanMilliseconds : null;
}
