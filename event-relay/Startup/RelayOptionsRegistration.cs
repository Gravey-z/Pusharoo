using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Services;
using Microsoft.Extensions.Options;

namespace Pusharoo.EventRelay.Startup;

internal static class RelayOptionsRegistration
{
    internal static void AddRelayOptions(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.Configure<MongoDbOptions>(configuration.GetSection(MongoDbOptions.SectionName));

        AddNeoRpcOptions(services, configuration);
        AddEventRelayOptions(services, configuration);
        services.AddSingleton<IValidateOptions<RelayPackageOptions>, RelayPackageOptionsValidator>();
        services.AddOptions<RelayPackageOptions>()
            .Bind(configuration.GetSection(RelayPackageOptions.SectionName)).ValidateOnStart();

        services.Configure<PusharooApiOptions>(
            configuration.GetSection(PusharooApiOptions.SectionName)
        );
    }

    private static void AddNeoRpcOptions(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<NeoRpcOptions>()
            .Bind(configuration.GetSection(NeoRpcOptions.SectionName))
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
                x =>
                    x.ScannerLeaseSeconds >= 3
                    && x.PollIntervalSeconds > 0
                    && x.MaxBlocksPerPoll > 0,
                "Invalid scanner lease/poll settings."
            )
            .Validate(
                x =>
                    x.MinRequestIntervalMilliseconds is >= 0 and <= 60000
                    && x.CatchUpPollIntervalMilliseconds is > 0 and <= 60000,
                "Invalid RPC pacing settings."
            )
            .ValidateOnStart();
    }

    private static void AddEventRelayOptions(
        IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<EventRelayOptions>()
            .Bind(configuration.GetSection(EventRelayOptions.SectionName))
            .Validate(
                x =>
                    x.WebhookTimeoutSeconds is > 0 and <= 60
                    && x.WebhookMaxAttempts is >= 1 and <= 100,
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
                x =>
                    x.FreeTestnetMaxEvents is >= 0 and <= 100000
                    && x.PaidMaxEvents is >= 0 and <= 100000,
                "Allowance periods support at most 100000 receipts."
            )
            .Validate(x => x.PaymentIntentMinutes is >= 1 and <= 60 && x.PaymentRecoveryPollSeconds is >= 1 and <= 300,
                "Invalid payment quote expiry or recovery polling interval.")
            .ValidateOnStart();
    }
}
