namespace Pusharoo.EventRelay.Services;

public sealed class RelayOperationsService
{
    private DateTime scannerHeartbeat = DateTime.MinValue;
    private DateTime workerHeartbeat = DateTime.MinValue;
    private long succeeded;
    private long failed;
    private long retried;
    private long deadLetters;

    public void ScannerHeartbeat() => scannerHeartbeat = DateTime.UtcNow;
    public void WorkerHeartbeat() => workerHeartbeat = DateTime.UtcNow;
    public void RecordDelivery(bool success, bool retry, bool deadLetter)
    {
        if (success) Interlocked.Increment(ref succeeded); else Interlocked.Increment(ref failed);
        if (retry) Interlocked.Increment(ref retried);
        if (deadLetter) Interlocked.Increment(ref deadLetters);
    }
    public RelayOperationsSnapshot Snapshot() => new(scannerHeartbeat, workerHeartbeat, Interlocked.Read(ref succeeded), Interlocked.Read(ref failed), Interlocked.Read(ref retried), Interlocked.Read(ref deadLetters));
}

public sealed record RelayOperationsSnapshot(DateTime ScannerHeartbeat, DateTime WorkerHeartbeat, long Succeeded, long Failed, long Retried, long DeadLetters);
