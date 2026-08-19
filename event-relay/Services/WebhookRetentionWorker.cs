using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookRetentionWorker(IServiceScopeFactory scopeFactory, IOptions<EventRelayOptions> options, ILogger<WebhookRetentionWorker> logger) : BackgroundService
{
    private readonly EventRelayOptions settings = options.Value;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IWebhookDeliveryRepository>().PurgeExpiredAsync(
                    DateTime.UtcNow.AddDays(-Math.Max(1, settings.DeliveryPayloadRetentionDays)),
                    DateTime.UtcNow.AddDays(-Math.Max(settings.DeliveryPayloadRetentionDays, settings.DeliveryHistoryRetentionDays)), stoppingToken);
            }
            catch (Exception ex) { logger.LogError(ex, "Webhook retention sweep failed."); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, settings.RetentionSweepMinutes)), stoppingToken);
        }
    }
}
