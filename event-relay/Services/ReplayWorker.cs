namespace Pusharoo.EventRelay.Services;

public sealed class ReplayWorker(
    IServiceScopeFactory scopes,
    ILogger<ReplayWorker> logger,
    TimeProvider? timeProvider = null
) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReplayScanner>().PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Replay worker will retry; checkpoint is durable.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), clock, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
