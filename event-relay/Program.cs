using Pusharoo.EventRelay.Services;
using Pusharoo.EventRelay.Startup;
using MongoDB.Bson;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPusharooEventRelay(builder.Configuration);

var app = builder.Build();

if (args.Contains("--billing-schema-inventory") || args.Contains("--billing-schema-baseline"))
{
    using var scope = app.Services.CreateScope();
    var store = scope.ServiceProvider.GetRequiredService<RelayPackageStore>();
    if (args.Contains("--billing-schema-baseline"))
    {
        var baselineId = builder.Configuration["billing-schema-baseline"];
        var sourceRevision = builder.Configuration["source-revision"];
        if (string.IsNullOrWhiteSpace(baselineId) || string.IsNullOrWhiteSpace(sourceRevision))
            throw new ArgumentException("Baseline ID and --source-revision are required; use verified historical settings, not guessed current terms.");
        await store.CaptureLegacyBaselineAsync(baselineId, sourceRevision, CancellationToken.None);
    }
    Console.WriteLine((await store.InventoryAsync(CancellationToken.None)).ToJson());
    return;
}

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
