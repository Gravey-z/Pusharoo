using System.Threading.RateLimiting;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Startup;

public static class RelayRateLimiting
{
    public const string Queries = "WebhookQueries";
    public const string Management = "WebhookManagement";

    public static IServiceCollection AddRelayManagementRateLimiting(
        this IServiceCollection services
    )
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Queries, context => CreatePartition(context, 120));
            options.AddPolicy(Management, context => CreatePartition(context, 30));
        });

        return services;
    }

    private static RateLimitPartition<string> CreatePartition(HttpContext context, int permitLimit)
    {
        var actor = RequireApiServiceAttribute.GetVerifiedActorScriptHash(context);
        var key = actor is null
            ? "unverified:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            : "wallet:" + actor;

        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }
        );
    }
}
