using Microsoft.AspNetCore.DataProtection;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Startup;

public static class RelayServiceRegistration
{
    public static IServiceCollection AddPusharooEventRelay(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddRelayOptions(configuration);
        AddApplicationServices(services);
        AddSecretProtection(services, configuration);
        AddScanningAndDelivery(services);
        AddHttpServices(services);
        return services;
    }

    private static void AddApplicationServices(IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MongoDbContext>();
        services.AddSingleton<RelayPackageCatalog>();
        services.AddScoped<RelayPackageStore>();
        services.AddHostedService<RelayCatalogReadinessWorker>();
        services.AddScoped<IWebhookSubscriptionRepository, WebhookSubscriptionRepository>();
        services.AddScoped<IWebhookDeliveryRepository, WebhookDeliveryRepository>();
        services.AddScoped<IEventCheckpointRepository, EventCheckpointRepository>();
        services.AddSingleton<WebhookDestinationValidator>();
        services.AddScoped<WebhookSubscriptionValidator>();
        services.AddScoped<SubscriptionManagementService>();
        services.AddSingleton<WebhookSecretProtector>();
        services.AddSingleton<RelayOperationsService>();
        services.AddSingleton<NeoRpcRequestLimiter>();
        services.AddScoped<RelayHealthService>();
        services.AddSingleton<RelayHealthCache>();
        services.AddHostedService<RelayAlertWorker>();
        services.AddScoped<RelayEntitlementService>();
        services.AddScoped<RelayLifecycleService>();
        services.AddHostedService<RelayLifecycleWorker>();
        services.AddScoped<RelayPaymentVerifier>();
        services.AddScoped<RelayPaymentService>();
        services.AddScoped<IRelayPaymentProjector, RelayPaymentProjector>();
        services.AddHostedService<RelayPaymentRecoveryWorker>();
    }

    private static void AddSecretProtection(
        IServiceCollection services,
        IConfiguration configuration
    )
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("Pusharoo.EventRelay");

        var keyRingPath = configuration["DataProtection:KeyRingPath"];

        if (!string.IsNullOrWhiteSpace(keyRingPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        }
    }

    private static void AddScanningAndDelivery(IServiceCollection services)
    {
        services.AddHttpClient<NeoRpcClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddScoped<INeoRpcClient>(services => services.GetRequiredService<NeoRpcClient>());
        services.AddScoped<IEventDeliveryQueue>(services =>
            services.GetRequiredService<WebhookDeliveryService>()
        );

        services.AddScoped<NeoBlockScanner>();
        services.AddScoped<ReplayJobRepository>();
        services.AddScoped<ReplayAdmissionRepository>();
        services.AddSingleton<ReplayAdmissionPolicy>();
        services.AddScoped<ReplayAdmissionService>();
        services.AddScoped<ReplayScanner>();
        services.AddHostedService<ReplayWorker>();

        services.AddScoped<WebhookDeliveryService>();

        services
            .AddHttpClient<IWebhookTransport, WebhookTransport>()
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectCallback = WebhookDestinationValidator.ConnectPublicAsync,
                }
            );

        services.AddHostedService<NeoEventMonitorService>();
        services.AddHostedService<WebhookDeliveryWorker>();
        services.AddHostedService<WebhookRetentionWorker>();
        services.AddHostedService<WebhookSecretMigrationService>();
    }

    private static void AddHttpServices(IServiceCollection services)
    {
        services.AddRelayManagementRateLimiting();

        services.AddControllers();
        services.AddOpenApi();
    }
}
