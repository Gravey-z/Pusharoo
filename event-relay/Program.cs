using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
using Pusharoo.EventRelay.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection(MongoDbOptions.SectionName));
builder.Services.Configure<NeoRpcOptions>(builder.Configuration.GetSection(NeoRpcOptions.SectionName));
builder.Services.Configure<EventRelayOptions>(builder.Configuration.GetSection(EventRelayOptions.SectionName));
builder.Services.Configure<PusharooApiOptions>(builder.Configuration.GetSection(PusharooApiOptions.SectionName));

builder.Services.AddSingleton<MongoDbContext>();
builder.Services.AddScoped<IWebhookSubscriptionRepository, WebhookSubscriptionRepository>();
builder.Services.AddScoped<IWebhookDeliveryRepository, WebhookDeliveryRepository>();
builder.Services.AddScoped<IEventCheckpointRepository, EventCheckpointRepository>();
builder.Services.AddSingleton<WebhookDestinationValidator>();
builder.Services.AddSingleton<WebhookSecretProtector>();
builder.Services.AddSingleton<WebhookSessionService>();
builder.Services.AddSingleton<RelayOperationsService>();
builder.Services.AddScoped<RelayEntitlementService>();
builder.Services.AddScoped<RelayPaymentService>();
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Pusharoo.EventRelay");
var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
}
builder.Services.AddHttpClient<NeoRpcClient>();
builder.Services.AddHttpClient<ProjectAccessClient>();
builder.Services.AddHttpClient<WebhookDeliveryService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = WebhookDestinationValidator.ConnectPublicAsync
    });
builder.Services.AddHostedService<NeoEventMonitorService>();
builder.Services.AddHostedService<WebhookDeliveryWorker>();
builder.Services.AddHostedService<WebhookRetentionWorker>();
builder.Services.AddHostedService<WebhookSecretMigrationService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("WebhookManagement", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
var allowedCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?.Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out _))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray() ?? [];
if (allowedCorsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(allowedCorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("X-Pusharoo-Webhook-Session");
    }));
}
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", async (
    IOptions<NeoRpcOptions> neoRpcOptions,
    IOptions<EventRelayOptions> relayOptions,
    RelayOperationsService operations,
    IWebhookDeliveryRepository deliveries,
    CancellationToken cancellationToken) =>
{
    var snapshot = operations.Snapshot();
    var configuration = relayOptions.Value;
    var now = DateTime.UtcNow;
    var scannerHealthy = snapshot.ScannerHeartbeat != DateTime.MinValue
        && now - snapshot.ScannerHeartbeat < TimeSpan.FromSeconds(Math.Max(1, configuration.ScannerStallSeconds));
    var workerHealthy = snapshot.WorkerHeartbeat != DateTime.MinValue
        && now - snapshot.WorkerHeartbeat < TimeSpan.FromSeconds(Math.Max(1, configuration.DeliveryWorkerStallSeconds));
    var rpcHealthy = snapshot.RpcSuccessAt != DateTime.MinValue
        && now - snapshot.RpcSuccessAt < TimeSpan.FromSeconds(Math.Max(1, configuration.ScannerStallSeconds));
    long queueDepth;
    try
    {
        queueDepth = await deliveries.CountOutstandingAsync(cancellationToken);
    }
    catch
    {
        return Results.Json(new { status = "degraded", network = neoRpcOptions.Value.Network, error = "Webhook queue health could not be read." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var scannerLagHealthy = snapshot.ScannerLagBlocks is null || snapshot.ScannerLagBlocks <= Math.Max(0, configuration.MaxScannerLagBlocks);
    var queueHealthy = queueDepth <= Math.Max(0, configuration.MaxQueueDepth);
    var healthy = scannerHealthy && workerHealthy && rpcHealthy && scannerLagHealthy && queueHealthy;
    return Results.Json(new
    {
        status = healthy ? "ok" : "degraded",
        network = neoRpcOptions.Value.Network,
        scannerHealthy,
        workerHealthy,
        rpcHealthy,
        scannerLagHealthy,
        queueHealthy,
        usingFallbackRpc = !string.IsNullOrWhiteSpace(neoRpcOptions.Value.FallbackEndpoint)
            && string.Equals(snapshot.ActiveRpcEndpoint, neoRpcOptions.Value.FallbackEndpoint, StringComparison.OrdinalIgnoreCase),
        metrics = new
        {
            scannerLagBlocks = snapshot.ScannerLagBlocks,
            queueDepth,
            snapshot.Succeeded,
            snapshot.Failed,
            successRate = snapshot.SuccessRate,
            snapshot.Retried,
            snapshot.DeadLetters,
            averageDeliveryLatencyMilliseconds = snapshot.AverageDeliveryLatencyMilliseconds,
            snapshot.RpcFailures
        }
    }, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/metrics", async (
    IOptions<NeoRpcOptions> neoRpcOptions,
    RelayOperationsService operations,
    IWebhookDeliveryRepository deliveries,
    CancellationToken cancellationToken) =>
{
    var snapshot = operations.Snapshot();
    var queueDepth = await deliveries.CountOutstandingAsync(cancellationToken);
    var network = neoRpcOptions.Value.Network.Replace("\\", "\\\\").Replace("\"", "\\\"");
    var metric = string.Join('\n', new[]
    {
        "# TYPE pusharoo_relay_scanner_lag_blocks gauge",
        $"pusharoo_relay_scanner_lag_blocks{{network=\"{network}\"}} {snapshot.ScannerLagBlocks ?? 0}",
        "# TYPE pusharoo_relay_queue_depth gauge",
        $"pusharoo_relay_queue_depth{{network=\"{network}\"}} {queueDepth}",
        "# TYPE pusharoo_relay_delivery_succeeded_total counter",
        $"pusharoo_relay_delivery_succeeded_total{{network=\"{network}\"}} {snapshot.Succeeded}",
        "# TYPE pusharoo_relay_delivery_failed_total counter",
        $"pusharoo_relay_delivery_failed_total{{network=\"{network}\"}} {snapshot.Failed}",
        "# TYPE pusharoo_relay_delivery_retried_total counter",
        $"pusharoo_relay_delivery_retried_total{{network=\"{network}\"}} {snapshot.Retried}",
        "# TYPE pusharoo_relay_delivery_dead_letters_total counter",
        $"pusharoo_relay_delivery_dead_letters_total{{network=\"{network}\"}} {snapshot.DeadLetters}",
        "# TYPE pusharoo_relay_delivery_latency_milliseconds gauge",
        $"pusharoo_relay_delivery_latency_milliseconds{{network=\"{network}\"}} {snapshot.AverageDeliveryLatencyMilliseconds ?? 0}",
        "# TYPE pusharoo_relay_rpc_failures_total counter",
        $"pusharoo_relay_rpc_failures_total{{network=\"{network}\"}} {snapshot.RpcFailures}"
    }) + "\n";
    return Results.Text(metric, "text/plain; version=0.0.4");
});
if (allowedCorsOrigins.Length > 0)
{
    app.UseCors("Frontend");
}
app.UseRateLimiter();
app.MapControllers();

app.Run();
