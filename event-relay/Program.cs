using Pusharoo.EventRelay.Services;
using Pusharoo.EventRelay.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPusharooEventRelay(builder.Configuration);

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
