namespace Pusharoo.EventRelay.Services;

public sealed class RelayOperationsService
{
    private DateTime scannerHeartbeat = DateTime.MinValue;
    private DateTime workerHeartbeat = DateTime.MinValue;
    private DateTime rpcSuccessAt = DateTime.MinValue;
    private string activeRpcEndpoint = string.Empty;
    private uint? confirmedBlock;
    private uint? processedBlock;
    private long succeeded;
    private long failed;
    private long retried;
    private long deadLetters;
    private long latencyTotalMilliseconds;
    private long latencySamples;
    private long rpcFailures;

    public void ScannerHeartbeat() => scannerHeartbeat = DateTime.UtcNow;
    public void WorkerHeartbeat() => workerHeartbeat = DateTime.UtcNow;

    public void RecordScannerProgress(uint confirmedTip, uint processedThrough)
    {
        confirmedBlock = confirmedTip;
        processedBlock = processedThrough;
    }

    public void RecordRpcSuccess(string endpoint)
    {
        activeRpcEndpoint = endpoint;
        rpcSuccessAt = DateTime.UtcNow;
    }

    public void RecordRpcFailure() => Interlocked.Increment(ref rpcFailures);

    public void RecordDelivery(bool success, bool retry, bool deadLetter, long latencyMilliseconds)
    {
        if (success) Interlocked.Increment(ref succeeded); else Interlocked.Increment(ref failed);
        if (retry) Interlocked.Increment(ref retried);
        if (deadLetter) Interlocked.Increment(ref deadLetters);
        if (latencyMilliseconds >= 0)
        {
            Interlocked.Add(ref latencyTotalMilliseconds, latencyMilliseconds);
            Interlocked.Increment(ref latencySamples);
        }
    }

    public RelayOperationsSnapshot Snapshot() => new(
        scannerHeartbeat,
        workerHeartbeat,
        rpcSuccessAt,
        activeRpcEndpoint,
        confirmedBlock,
        processedBlock,
        Interlocked.Read(ref succeeded),
        Interlocked.Read(ref failed),
        Interlocked.Read(ref retried),
        Interlocked.Read(ref deadLetters),
        Interlocked.Read(ref latencyTotalMilliseconds),
        Interlocked.Read(ref latencySamples),
        Interlocked.Read(ref rpcFailures));
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
    long RpcFailures)
{
    public uint? ScannerLagBlocks => ConfirmedBlock is not null && ProcessedBlock is not null
        ? ConfirmedBlock.Value >= ProcessedBlock.Value ? ConfirmedBlock.Value - ProcessedBlock.Value : 0
        : null;

    public double? AverageDeliveryLatencyMilliseconds => LatencySamples > 0
        ? (double)LatencyTotalMilliseconds / LatencySamples
        : null;

    public double? SuccessRate => Succeeded + Failed > 0
        ? (double)Succeeded / (Succeeded + Failed)
        : null;
}
