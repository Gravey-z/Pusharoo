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

app.MapGet("/health", (IOptions<NeoRpcOptions> neoRpcOptions, RelayOperationsService operations) =>
{
    var snapshot = operations.Snapshot();
    var now = DateTime.UtcNow;
    var scannerHealthy = snapshot.ScannerHeartbeat != DateTime.MinValue && now - snapshot.ScannerHeartbeat < TimeSpan.FromMinutes(2);
    var workerHealthy = snapshot.WorkerHeartbeat != DateTime.MinValue && now - snapshot.WorkerHeartbeat < TimeSpan.FromMinutes(2);
    return Results.Json(new { status = scannerHealthy && workerHealthy ? "ok" : "degraded", network = neoRpcOptions.Value.Network, scannerHealthy, workerHealthy, metrics = new { snapshot.Succeeded, snapshot.Failed, snapshot.Retried, snapshot.DeadLetters } }, statusCode: scannerHealthy && workerHealthy ? 200 : 503);
});
if (allowedCorsOrigins.Length > 0)
{
    app.UseCors("Frontend");
}
app.UseRateLimiter();
app.MapControllers();

app.Run();
