using System.Text.Json;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed class NeoEventMonitorService(
    IServiceScopeFactory scopeFactory,
    NeoRpcClient neoRpc,
    IOptions<NeoRpcOptions> options,
    ILogger<NeoEventMonitorService> logger) : BackgroundService
{
    private readonly NeoRpcOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Neo event relay starting for {Network} at {Endpoint}.",
            _options.Network,
            _options.Endpoint);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Neo event polling failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)), stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IEventCheckpointRepository>();
        var subscriptions = scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionRepository>();
        var webhookDelivery = scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>();

        var blockCount = await neoRpc.GetBlockCountAsync(cancellationToken);
        if (blockCount == 0)
        {
            return;
        }

        var tip = blockCount - 1;
        var checkpointId = $"neo:{_options.Network}";
        var checkpoint = await checkpoints.GetAsync(checkpointId, cancellationToken);
        var nextBlock = checkpoint?.NextBlock ?? _options.StartBlock ?? tip;

        if (nextBlock > tip)
        {
            logger.LogDebug("Neo event relay is caught up at block {Tip}.", tip);
            return;
        }

        var maxBlocks = Math.Max(1, _options.MaxBlocksPerPoll);
        var toBlock = Math.Min(tip, nextBlock + (uint)maxBlocks - 1);

        for (var blockIndex = nextBlock; blockIndex <= toBlock; blockIndex++)
        {
            var transactionHashes = await neoRpc.GetBlockTransactionsAsync(blockIndex, cancellationToken);
            foreach (var transactionHash in transactionHashes)
            {
                JsonElement applicationLog;
                try
                {
                    applicationLog = await neoRpc.GetApplicationLogAsync(transactionHash, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "No application log was available for transaction {TransactionHash}.",
                        transactionHash);
                    continue;
                }

                foreach (var observedEvent in ExtractEvents(applicationLog, transactionHash, blockIndex))
                {
                    var matches = await subscriptions.GetMatchingAsync(
                        observedEvent.ContractHash,
                        observedEvent.EventName,
                        cancellationToken);

                    foreach (var subscription in matches)
                    {
                        await webhookDelivery.DeliverAsync(subscription, observedEvent, cancellationToken);
                    }
                }
            }

            await checkpoints.UpsertAsync(
                new EventCheckpointDocument
                {
                    Id = checkpointId,
                    NextBlock = blockIndex + 1,
                    UpdatedAt = DateTime.UtcNow
                },
                cancellationToken);

            logger.LogInformation("Neo event relay checkpoint advanced to block {NextBlock}.", blockIndex + 1);
        }
    }

    private IEnumerable<ObservedNeoEvent> ExtractEvents(
        JsonElement applicationLog,
        string transactionHash,
        uint blockIndex)
    {
        if (!applicationLog.TryGetProperty("executions", out var executions)
            || executions.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var eventIndex = 0;
        foreach (var execution in executions.EnumerateArray())
        {
            if (!execution.TryGetProperty("notifications", out var notifications)
                || notifications.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var notification in notifications.EnumerateArray())
            {
                var contractHash = GetString(notification, "contract")
                    ?? GetString(notification, "scripthash");
                var eventName = GetString(notification, "eventname")
                    ?? GetString(notification, "eventName");

                if (string.IsNullOrWhiteSpace(contractHash) || string.IsNullOrWhiteSpace(eventName))
                {
                    continue;
                }

                var state = notification.TryGetProperty("state", out var stateElement)
                    ? stateElement.Clone()
                    : JsonSerializer.SerializeToElement<object?>(null);

                yield return new ObservedNeoEvent(
                    $"{_options.Network}:{blockIndex}:{transactionHash}:{eventIndex}",
                    _options.Network,
                    blockIndex,
                    transactionHash,
                    contractHash.Trim().ToLowerInvariant(),
                    eventName.Trim(),
                    state,
                    DateTime.UtcNow);

                eventIndex++;
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }
}
