using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookDeliveryWorker(IServiceScopeFactory scopeFactory, RelayOperationsService operations) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var delivery = scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>();
            var subscriptions = scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionRepository>();
            operations.WorkerHeartbeat();
            if (!await delivery.ProcessNextAsync(subscriptions, stoppingToken)) await Task.Delay(500, stoppingToken);
        }
    }
}
