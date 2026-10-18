using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
using Pusharoo.EventRelay.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MongoDbOptions>(
    builder.Configuration.GetSection(MongoDbOptions.SectionName)
);

builder
    .Services.AddOptions<NeoRpcOptions>()
    .Bind(builder.Configuration.GetSection(NeoRpcOptions.SectionName))
    .Validate(
        x => x.Network is "neo3:mainnet" or "neo3:testnet",
        "NeoRpc network must be neo3:mainnet or neo3:testnet."
    )
    .Validate(
        x =>
            Uri.TryCreate(x.Endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https",
        "NeoRpc endpoint must be HTTP(S)."
    )
    .Validate(
        x =>
            string.IsNullOrWhiteSpace(x.FallbackEndpoint)
            || (
                Uri.TryCreate(x.FallbackEndpoint, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
            ),
        "NeoRpc fallback must be HTTP(S)."
    )
    .Validate(
        x =>
            x.RequestTimeoutSeconds > 0
            && x.MaxAttempts is >= 1 and <= 10
            && x.RetryBaseMilliseconds >= 0
            && x.RetryMaxMilliseconds >= x.RetryBaseMilliseconds,
        "Invalid NeoRpc timeout/retry settings."
    )
    .Validate(
        x => x.ScannerLeaseSeconds >= 3 && x.PollIntervalSeconds > 0 && x.MaxBlocksPerPoll > 0,
        "Invalid scanner lease/poll settings."
    )
    .Validate(
        x =>
            x.MinRequestIntervalMilliseconds is >= 0 and <= 60000
            && x.CatchUpPollIntervalMilliseconds is > 0 and <= 60000,
        "Invalid RPC pacing settings."
    )
    .ValidateOnStart();

builder
    .Services.AddOptions<EventRelayOptions>()
    .Bind(builder.Configuration.GetSection(EventRelayOptions.SectionName))
    .Validate(
        x => x.WebhookTimeoutSeconds is > 0 and <= 60 && x.WebhookMaxAttempts is >= 1 and <= 100,
        "Delivery timeout must be at most 60 seconds and attempts at most 100."
    )
    .Validate(
        x =>
            x.ReplayMaxRangeBlocks is > 0 and <= 100000
            && x.ReplayMaxBlocksPerPoll is > 0 and <= 100
            && x.MaxQueueDepth > 0,
        "Invalid replay workload limits."
    )
    .Validate(
        x =>
            x.ReplayProjectBlocksPerDay >= x.ReplayMaxRangeBlocks
            && x.ReplayProjectBlocksPerDay <= 1000000
            && x.ReplayCooldownSeconds is >= 1 and <= 86400
            && x.ReplayMaxActiveJobsPerNetwork is >= 1 and <= 100
            && x.ReplayMaxJobsPerDayPerNetwork >= x.ReplayMaxActiveJobsPerNetwork
            && x.ReplayMaxJobsPerDayPerNetwork <= 1000,
        "Invalid replay quota or cooldown limits."
    )
    .Validate(
        x =>
            x.OperationsPollSeconds > 0
            && x.ScannerStallSeconds > 0
            && x.DeliveryWorkerStallSeconds > 0
            && x.DeliveryFailureWindowMinutes > 0
            && x.DeliveryFailureMinimumAttempts > 0
            && x.DeliveryFailureRateThreshold is > 0 and <= 1,
        "Invalid operations thresholds."
    )
    .Validate(
        x => x.FreeTestnetMaxEvents is >= 0 and <= 100000 && x.PaidMaxEvents is >= 0 and <= 100000,
        "Allowance periods support at most 100000 receipts."
    )
    .ValidateOnStart();

builder.Services.Configure<PusharooApiOptions>(
    builder.Configuration.GetSection(PusharooApiOptions.SectionName)
);

builder.Services.AddSingleton<MongoDbContext>();
builder.Services.AddScoped<IWebhookSubscriptionRepository, WebhookSubscriptionRepository>();
builder.Services.AddScoped<IWebhookDeliveryRepository, WebhookDeliveryRepository>();
builder.Services.AddScoped<IEventCheckpointRepository, EventCheckpointRepository>();
builder.Services.AddSingleton<WebhookDestinationValidator>();
builder.Services.AddScoped<WebhookSubscriptionValidator>();
builder.Services.AddScoped<SubscriptionManagementService>();
builder.Services.AddSingleton<WebhookSecretProtector>();
builder.Services.AddSingleton<RelayOperationsService>();
builder.Services.AddSingleton<NeoRpcRequestLimiter>();
builder.Services.AddScoped<RelayHealthService>();
builder.Services.AddSingleton<RelayHealthCache>();
builder.Services.AddHostedService<RelayAlertWorker>();
builder.Services.AddScoped<RelayEntitlementService>();
builder.Services.AddScoped<RelayPaymentVerifier>();
builder.Services.AddScoped<RelayPaymentService>();

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Pusharoo.EventRelay");

var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];

if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
}

builder.Services.AddHttpClient<NeoRpcClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddScoped<INeoRpcClient>(services => services.GetRequiredService<NeoRpcClient>());
builder.Services.AddScoped<IEventDeliveryQueue>(services =>
    services.GetRequiredService<WebhookDeliveryService>()
);

builder.Services.AddScoped<NeoBlockScanner>();
builder.Services.AddScoped<ReplayJobRepository>();
builder.Services.AddScoped<ReplayScanner>();
builder.Services.AddHostedService<ReplayWorker>();

builder.Services.AddScoped<WebhookDeliveryService>();

builder
    .Services.AddHttpClient<IWebhookTransport, WebhookTransport>()
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = WebhookDestinationValidator.ConnectPublicAsync
        }
    );

builder.Services.AddHostedService<NeoEventMonitorService>();
builder.Services.AddHostedService<WebhookDeliveryWorker>();
builder.Services.AddHostedService<WebhookRetentionWorker>();
builder.Services.AddHostedService<WebhookSecretMigrationService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(
        "WebhookManagement",
        context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }
            )
    );
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet(
    "/health",
    async (IServiceScopeFactory scopes, CancellationToken ct) =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            var status = await scope
                .ServiceProvider.GetRequiredService<RelayHealthService>()
                .GetAsync(ct);

            return Results.Json(status, statusCode: status.Status == "ok" ? 200 : 503);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Json(
                new
                {
                    status = "degraded",
                    error = "Relay operations database checks are unavailable."
                },
                statusCode: 503
            );
        }
    }
);

app.MapGet(
    "/metrics",
    async (IServiceScopeFactory scopes, CancellationToken ct) =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            var health = await scope
                .ServiceProvider.GetRequiredService<RelayHealthService>()
                .GetAsync(ct);

            return Results.Text(RelayMetrics.Render(health), "text/plain; version=0.0.4");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.StatusCode(503);
        }
    }
);

app.UseRateLimiter();

app.MapControllers();

app.Run();
