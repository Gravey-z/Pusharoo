using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

// Alerts are exported as status/Prometheus signals and logged; external notification is configured separately.

public sealed class RelayAlertWorker(
    IServiceScopeFactory scopes,
    IOptions<EventRelayOptions> options,
    ILogger<RelayAlertWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var previous = new HashSet<string>();
        var repeatedAt = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var status = await scope
                    .ServiceProvider.GetRequiredService<RelayHealthService>()
                    .GetAsync(ct);
                var current = status.Alerts.Select(x => x.Code).ToHashSet();

                foreach (var alert in status.Alerts)
                    if (
                        !previous.Contains(alert.Code)
                        || DateTime.UtcNow - repeatedAt > TimeSpan.FromMinutes(5)
                    )
                        logger.LogWarning(
                            "Relay alert {Network} {Code} {Severity}: {Message}",
                            status.Network,
                            alert.Code,
                            alert.Severity,
                            alert.Message
                        );

                foreach (var recovered in previous.Except(current))
                    logger.LogInformation("Relay alert recovered: {Code}.", recovered);

                if (
                    !current.SetEquals(previous)
                    || DateTime.UtcNow - repeatedAt > TimeSpan.FromMinutes(5)
                )
                    repeatedAt = DateTime.UtcNow;

                previous = current;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Relay operations database checks failed; monitoring will retry."
                );
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.OperationsPollSeconds), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
