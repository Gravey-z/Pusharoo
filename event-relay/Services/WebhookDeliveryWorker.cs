using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    RelayOperationsService operations,
    ILogger<WebhookDeliveryWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository =
                    scope.ServiceProvider.GetRequiredService<IWebhookDeliveryRepository>();
                await repository.FlushAuditAsync(stoppingToken);
                var delivery = scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>();
                var subscriptions =
                    scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionRepository>();
                var processed = await delivery.ProcessNextAsync(subscriptions, stoppingToken);
                operations.WorkerHeartbeat();

                if (!processed)
                    await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Delivery worker failed; durable claims will recover after expiry. Retrying."
                );

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
