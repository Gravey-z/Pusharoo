using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EventRelayOptions> options,
    ILogger<WebhookRetentionWorker> logger,
    TimeProvider? timeProvider = null
) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private readonly EventRelayOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var deliveryRepository =
                    scope.ServiceProvider.GetRequiredService<IWebhookDeliveryRepository>();
                var subscriptionRepository =
                    scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionRepository>();
                var freeTrials = await scope
                    .ServiceProvider.GetRequiredService<RelayEntitlementService>()
                    .ReconcileFreeTestnetTrialsAsync(stoppingToken);

                foreach (var trial in freeTrials)
                {
                    await subscriptionRepository.SetFreeTestnetSubscriptionExpiryAsync(
                        trial.ProjectId,
                        settings.TestnetSubscriptionRetentionDays,
                        stoppingToken
                    );
                }

                var expiredSubscriptions = await subscriptionRepository.DeleteExpiredAsync(
                    clock.GetUtcNow().UtcDateTime,
                    stoppingToken
                );
                await deliveryRepository.DeleteBySubscriptionIdsAsync(
                    expiredSubscriptions,
                    stoppingToken
                );
                await deliveryRepository.PurgeExpiredAsync(
                    clock
                        .GetUtcNow()
                        .UtcDateTime.AddDays(-Math.Max(1, settings.DeliveryPayloadRetentionDays)),
                    clock
                        .GetUtcNow()
                        .UtcDateTime.AddDays(
                            -Math.Max(
                                settings.DeliveryPayloadRetentionDays,
                                settings.DeliveryHistoryRetentionDays
                            )
                        ),
                    stoppingToken
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook retention sweep failed.");
            }

            await Task.Delay(
                TimeSpan.FromMinutes(Math.Max(5, settings.RetentionSweepMinutes)),
                clock,
                stoppingToken
            );
        }
    }
}
