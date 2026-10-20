using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class NeoRpcRequestLimiter(
    IOptions<NeoRpcOptions> options,
    TimeProvider? timeProvider = null
) : IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private readonly SemaphoreSlim gate = new(1, 1);

    private long next;

    public async Task WaitAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);

        try
        {
            var remaining = next - clock.GetTimestamp();

            if (remaining > 0)
                await Task.Delay(
                    TimeSpan.FromSeconds((double)remaining / clock.TimestampFrequency),
                    clock,
                    ct
                );

            ct.ThrowIfCancellationRequested();
            next =
                clock.GetTimestamp()
                + (long)(
                    options.Value.MinRequestIntervalMilliseconds / 1000d * clock.TimestampFrequency
                );
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
