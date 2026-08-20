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
                var deliveryRepository = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryRepository>();
                var subscriptionRepository = scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionRepository>();
                var freeTrials = await scope.ServiceProvider.GetRequiredService<RelayEntitlementService>().ReconcileFreeTestnetTrialsAsync(stoppingToken);
                foreach (var trial in freeTrials)
                {
                    await subscriptionRepository.SetFreeTestnetSubscriptionExpiryAsync(
                        trial.ProjectId,
                        settings.TestnetSubscriptionRetentionDays,
                        stoppingToken);
                }
                var expiredSubscriptions = await subscriptionRepository.DeleteExpiredAsync(DateTime.UtcNow, stoppingToken);
                await deliveryRepository.DeleteBySubscriptionIdsAsync(expiredSubscriptions, stoppingToken);
                await deliveryRepository.PurgeExpiredAsync(
                    DateTime.UtcNow.AddDays(-Math.Max(1, settings.DeliveryPayloadRetentionDays)),
                    DateTime.UtcNow.AddDays(-Math.Max(settings.DeliveryPayloadRetentionDays, settings.DeliveryHistoryRetentionDays)), stoppingToken);
            }
            catch (Exception ex) { logger.LogError(ex, "Webhook retention sweep failed."); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, settings.RetentionSweepMinutes)), stoppingToken);
        }
    }
}
