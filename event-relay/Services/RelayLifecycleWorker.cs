namespace Pusharoo.EventRelay.Services;

public sealed class RelayLifecycleWorker(
    IServiceScopeFactory scopes,
    ILogger<RelayLifecycleWorker> logger,
    TimeProvider? timeProvider = null
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clock = timeProvider ?? TimeProvider.System;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();

                await scope
                    .ServiceProvider.GetRequiredService<RelayLifecycleService>()
                    .ReconcileDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(
                    e,
                    "Relay lifecycle reconciliation failed; durable work is retained."
                );
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
