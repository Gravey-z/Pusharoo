using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;
namespace Pusharoo.EventRelay.Services;

public sealed class NeoEventMonitorService(IServiceScopeFactory scopeFactory, IOptions<NeoRpcOptions> options,
    ILogger<NeoEventMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<NeoBlockScanner>().PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Neo scanner poll failed; its checkpoint has not skipped the failed work."); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
