using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class RelayPaymentRecoveryWorker(
    IServiceScopeFactory scopes,
    IOptions<NeoRpcOptions> network,
    IOptions<EventRelayOptions> options,
    ILogger<RelayPaymentRecoveryWorker> logger,
    TimeProvider? timeProvider = null
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (network.Value.Network != "neo3:mainnet")
            return;

        var clock = timeProvider ?? TimeProvider.System;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var counts = await scope
                    .ServiceProvider.GetRequiredService<RelayPaymentService>()
                    .RecoverPendingAsync(stoppingToken);

                if (counts.Deferred > 0)
                    logger.LogWarning(
                        "Relay payment recovery deferred {Count} payments; durable state is retained.",
                        counts.Deferred
                    );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogError(
                    "Relay payment recovery could not read pending work; it will retry without discarding payments."
                );
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.PaymentRecoveryPollSeconds),
                    clock,
                    stoppingToken
                );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
