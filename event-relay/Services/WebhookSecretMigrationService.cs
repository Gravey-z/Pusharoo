using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookSecretMigrationService(
    IServiceScopeFactory scopeFactory,
    WebhookSecretProtector secretProtector,
    ILogger<WebhookSecretMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var subscriptions = scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionRepository>();
        var existingSubscriptions = await subscriptions.GetAllAsync(cancellationToken);
        var migratedCount = 0;

        foreach (var subscription in existingSubscriptions)
        {
            if (string.IsNullOrWhiteSpace(subscription.Secret)
                || subscription.Secret.StartsWith("protected:v1:", StringComparison.Ordinal))
            {
                continue;
            }

            var migrated = new Models.WebhookSubscriptionDocument
            {
                Id = subscription.Id,
                ProjectId = subscription.ProjectId,
                Name = subscription.Name,
                ContractHash = subscription.ContractHash,
                EventName = subscription.EventName,
                WebhookUrl = subscription.WebhookUrl,
                Secret = secretProtector.Protect(subscription.Secret),
                Headers = subscription.Headers,
                IsEnabled = subscription.IsEnabled,
                CreatedAt = subscription.CreatedAt,
                UpdatedAt = DateTime.UtcNow
            };

            await subscriptions.ReplaceAsync(migrated, cancellationToken);
            migratedCount++;
        }

        if (migratedCount > 0)
        {
            logger.LogInformation("Migrated {Count} webhook signing secrets to protected storage.", migratedCount);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
