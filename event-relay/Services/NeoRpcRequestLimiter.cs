using System.Diagnostics;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;
namespace Pusharoo.EventRelay.Services;

// Shared across live scanning, replay and payments within one relay replica.
public sealed class NeoRpcRequestLimiter(IOptions<NeoRpcOptions> options) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long next;
    public async Task WaitAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var remaining = next - Stopwatch.GetTimestamp();
            if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), ct);
            ct.ThrowIfCancellationRequested();
            next = Stopwatch.GetTimestamp() + (long)(options.Value.MinRequestIntervalMilliseconds / 1000d * Stopwatch.Frequency);
        }
        finally { gate.Release(); }
    }
    public void Dispose() => gate.Dispose();
}
