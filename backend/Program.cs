using backend.Services;
using backend.Startup;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.AddPusharooBackend();

var app = builder.Build();

app.Use(
    async (context, next) =>
    {
        try
        {
            await next();
        }
        catch (Exception error)
            when ((error is MongoException or TimeoutException)
                && context.Request.Path.StartsWithSegments("/api/auth")
                && !context.Response.HasStarted
            )
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(
                new
                {
                    code = "auth_unavailable",
                    error = "Wallet authentication is temporarily unavailable.",
                }
            );
        }
    }
);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (builder.Configuration.GetValue<bool>("Https:RedirectEnabled"))
{
    app.UseHttpsRedirection();
}

app.Use(
    (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/faucet"))
            context
                .RequestServices.GetRequiredService<FaucetClientIpResolver>()
                .CaptureDirectCloudflareIp(context);

        return next(context);
    }
);

app.UseForwardedHeaders();

if (app.Configuration.GetFrontendCorsOrigins().Length > 0)
{
    app.UseCors("Frontend");
}

app.UseRateLimiter();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.MapGet(
    "/api/relay/{network}/health",
    async (
        string network,
        RelayGatewayService relay,
        HttpContext context,
        CancellationToken cancellationToken
    ) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await relay.GetHealthAsync(network, cancellationToken);

        return Results.Content(result.Content, "application/json", statusCode: result.StatusCode);
    }
);

app.MapPost(
    "/api/faucet/challenges",
    (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";

        return Results.Json(
            new
            {
                code = "client_outdated",
                error = "Pusharoo was updated. Reload this page and sign in again.",
            },
            statusCode: StatusCodes.Status410Gone
        );
    }
);

app.MapPost(
    "/api/projects/{projectId}/deployments/authorization-challenge",
    (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";

        return Results.Json(
            new
            {
                code = "client_outdated",
                error = "Pusharoo was updated. Reload this page and sign in again.",
            },
            statusCode: StatusCodes.Status410Gone
        );
    }
);

app.MapPost(
    "/api/projects/{projectId}/webhook-access/validate",
    (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";

        return Results.Json(
            new
            {
                code = "client_outdated",
                error = "Pusharoo was updated. Reload this page and sign in again.",
            },
            statusCode: StatusCodes.Status410Gone
        );
    }
);

app.Run();
