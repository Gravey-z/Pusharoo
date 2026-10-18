namespace Pusharoo.EventRelay.Services;

// Coalesce public health and scrape traffic; a brief cache avoids repeated history aggregations.

public sealed class RelayHealthCache(TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private readonly SemaphoreSlim gate = new(1, 1);

    private RelayHealthSnapshot? value;

    private DateTime expires;

    public async Task<RelayHealthSnapshot> GetAsync(
        Func<CancellationToken, Task<RelayHealthSnapshot>> read,
        CancellationToken ct
    )
    {
        await gate.WaitAsync(ct);

        try
        {
            if (value is not null && clock.GetUtcNow().UtcDateTime < expires)
                return value;

            var fresh = await read(ct); // Failed reads are never reported as a fresh healthy sample.
            value = fresh;
            expires = clock.GetUtcNow().UtcDateTime.AddSeconds(5);

            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
