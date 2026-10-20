using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;

namespace Pusharoo.EventRelay.Services;

public sealed record RelayAlert(string Code, string Severity, string Message);

public sealed record DeliveryQueueStatus(
    long Pending,
    long Retrying,
    long Waiting,
    long Delivering,
    long ExpiredClaims,
    long DeadLetters,
    DateTime? OldestOutstandingAt,
    long RecentSucceeded,
    long RecentFailed,
    long AuditPending
)
{
    public long Depth => Pending + Retrying + Waiting + Delivering;
}

public sealed record ReplayStatusSummary(
    long Running,
    long Paused,
    long Blocked,
    long Completed,
    long Cancelled,
    long CompletedBlocks,
    long TotalBlocks
);

public sealed record ScannerStatus(
    uint? NetworkTip,
    uint? ConfirmedTip,
    uint? NextBlock,
    uint? ScannedHeight,
    uint? LagBlocks,
    DateTime? LastSuccessfulScanAt,
    DateTime? LastPollAt,
    ScannerBlockingError? Error,
    double? BlocksPerSecond
);

public sealed record RelayHealthSnapshot(
    string Status,
    string Network,
    bool ScannerHealthy,
    bool WorkerHealthy,
    bool RpcHealthy,
    bool ScannerLagHealthy,
    bool QueueHealthy,
    bool UsingFallbackRpc,
    ScannerStatus Scanner,
    DeliveryQueueStatus Queue,
    ReplayStatusSummary Replays,
    IReadOnlyList<RelayAlert> Alerts,
    [property: JsonIgnore] RelayOperationsSnapshot Metrics
)
{
    public ScannerBlockingError? ScannerError => Scanner.Error;

    [JsonPropertyName("metrics")]
    public object PublicMetrics =>
        new
        {
            scannerLagBlocks = Scanner.LagBlocks,
            queueDepth = Queue.Depth,
            Metrics.Succeeded,
            Metrics.Failed,
            Metrics.SuccessRate,
            Metrics.Retried,
            Metrics.DeadLetters,
            Metrics.AverageDeliveryLatencyMilliseconds,
            Metrics.RpcFailures,
        };
}

public sealed class RelayHealthService(
    MongoDbContext db,
    IEventCheckpointRepository checkpoints,
    RelayOperationsService operations,
    IOptions<NeoRpcOptions> rpcOptions,
    IOptions<EventRelayOptions> relayOptions,
    RelayHealthCache? cache = null,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<RelayHealthSnapshot> GetAsync(CancellationToken ct) =>
        cache is null ? ReadAsync(ct) : cache.GetAsync(ReadAsync, ct);

    private async Task<RelayHealthSnapshot> ReadAsync(CancellationToken ct)
    {
        var network = rpcOptions.Value.Network;
        var checkpoint = await checkpoints.GetAsync("neo:" + network, ct);

        var subscriptionIds = await db
            .Subscriptions.Find(x => x.Network == network)
            .Project(x => x.Id)
            .ToListAsync(ct);
        var deliveryFilter =
            Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.Network, network)
            | (
                Builders<WebhookDeliveryDocument>.Filter.In(x => x.SubscriptionId, subscriptionIds)
                & (
                    Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.Network, "")
                    | Builders<WebhookDeliveryDocument>.Filter.Exists("network", false)
                )
            );
        deliveryFilter &= Builders<WebhookDeliveryDocument>.Filter.Ne(
            x => x.Status,
            "deduplicated"
        );
        var counts = await db
            .Deliveries.Aggregate()
            .Match(deliveryFilter)
            .Group<BsonDocument>(
                new BsonDocument { { "_id", "$status" }, { "count", new BsonDocument("$sum", 1) } }
            )
            .ToListAsync(ct);

        long Count(string status) =>
            counts.FirstOrDefault(x => x["_id"] == status)?.GetValue("count", 0).ToInt64() ?? 0;

        var outstanding =
            deliveryFilter
            & Builders<WebhookDeliveryDocument>.Filter.In(
                x => x.Status,
                new[] { "pending", "retrying", "waiting", "delivering" }
            );
        var oldest = await db
            .Deliveries.Find(outstanding)
            .SortBy(x => x.DeliveredAt)
            .Project(x => (DateTime?)x.DeliveredAt)
            .FirstOrDefaultAsync(ct);
        var expired = await db.Deliveries.CountDocumentsAsync(
            deliveryFilter
                & Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.Status, "delivering")
                & new BsonDocument(
                    "$expr",
                    new BsonDocument("$lte", new BsonArray { "$leaseUntil", "$$NOW" })
                ),
            cancellationToken: ct
        );
        var auditPending = await db.Deliveries.CountDocumentsAsync(
            deliveryFilter & Builders<WebhookDeliveryDocument>.Filter.Eq(x => x.AuditPending, true),
            cancellationToken: ct
        );
        var since = clock
            .GetUtcNow()
            .UtcDateTime.AddMinutes(-relayOptions.Value.DeliveryFailureWindowMinutes);

        var recent = await db
            .Deliveries.Aggregate()
            .Match(
                deliveryFilter
                    & Builders<WebhookDeliveryDocument>.Filter.Gte(x => x.DeliveredAt, since)
            )
            .AppendStage<BsonDocument>(new BsonDocument("$unwind", "$attempts"))
            .Match(new BsonDocument("attempts.attemptedAt", new BsonDocument("$gte", since)))
            .Group<BsonDocument>(
                new BsonDocument
                {
                    { "_id", "$attempts.succeeded" },
                    { "count", new BsonDocument("$sum", 1) },
                }
            )
            .ToListAsync(ct);

        long Recent(bool success) =>
            recent.FirstOrDefault(x => x["_id"] == success)?.GetValue("count", 0).ToInt64() ?? 0;

        var queue = new DeliveryQueueStatus(
            Count("pending"),
            Count("retrying"),
            Count("waiting"),
            Count("delivering"),
            expired,
            Count("dead_letter"),
            oldest,
            Recent(true),
            Recent(false),
            auditPending
        );
        var replayCounts = await db
            .ReplayJobs.Aggregate()
            .Match(x => x.Network == network)
            .Group<BsonDocument>(
                new BsonDocument
                {
                    { "_id", "$status" },
                    { "count", new BsonDocument("$sum", 1) },
                    {
                        "completed",
                        new BsonDocument(
                            "$sum",
                            new BsonDocument(
                                "$subtract",
                                new BsonArray { "$nextBlock", "$fromBlock" }
                            )
                        )
                    },
                    {
                        "total",
                        new BsonDocument(
                            "$sum",
                            new BsonDocument(
                                "$add",
                                new BsonArray
                                {
                                    new BsonDocument(
                                        "$subtract",
                                        new BsonArray { "$toBlock", "$fromBlock" }
                                    ),
                                    1,
                                }
                            )
                        )
                    },
                }
            )
            .ToListAsync(ct);

        long Jobs(string status) =>
            replayCounts.FirstOrDefault(x => x["_id"] == status)?.GetValue("count", 0).ToInt64()
            ?? 0;

        var replays = new ReplayStatusSummary(
            Jobs("running"),
            Jobs("paused"),
            Jobs("blocked"),
            Jobs("completed"),
            Jobs("cancelled"),
            replayCounts.Sum(x => x["completed"].ToInt64()),
            replayCounts.Sum(x => x["total"].ToInt64())
        );

        return Evaluate(
            checkpoint,
            operations.Snapshot(),
            queue,
            replays,
            rpcOptions.Value,
            relayOptions.Value,
            clock.GetUtcNow().UtcDateTime
        );
    }

    public static RelayHealthSnapshot Evaluate(
        EventCheckpointDocument? checkpoint,
        RelayOperationsSnapshot sample,
        DeliveryQueueStatus queue,
        ReplayStatusSummary replays,
        NeoRpcOptions rpc,
        EventRelayOptions options,
        DateTime now
    )
    {
        var next = checkpoint?.NextBlock;
        uint? scanned = checkpoint?.LastSuccessfulScanAt is not null && next > 0 ? next - 1 : null;
        var tip = checkpoint?.ConfirmedTip ?? sample.ConfirmedBlock;
        uint? lag =
            tip is { } confirmed && next is { } height
                ? (uint)Math.Max(0, (long)confirmed - height + 1)
                : null;
        var poll = checkpoint?.LastPollAt;
        var error =
            checkpoint?.BlockingError
            ?? (sample.ScannerError?.OccurredAt >= poll ? sample.ScannerError : null);

        var publicError = error is null
            ? null
            : error with
            {
                Endpoint = PublicEndpoint(error.Endpoint),
                Message =
                    "Scanner retained this block. Inspect relay logs and the recovery runbook.",
            };
        var activity =
            checkpoint?.LastSuccessfulScanAt > poll ? checkpoint.LastSuccessfulScanAt : poll;
        var fresh =
            activity is { } observed
            && now - observed < TimeSpan.FromSeconds(options.ScannerStallSeconds);
        var scannerHealthy = fresh && next is not null && error is null;
        var workerHealthy =
            sample.WorkerHeartbeat != DateTime.MinValue
            && now - sample.WorkerHeartbeat
                < TimeSpan.FromSeconds(options.DeliveryWorkerStallSeconds);
        var lagHealthy = lag is not null && lag <= options.MaxScannerLagBlocks;
        var queueHealthy = queue.Depth <= options.MaxQueueDepth;
        var alerts = new List<RelayAlert>();

        if (!scannerHealthy)
            alerts.Add(
                new(
                    "scanner_stalled",
                    "critical",
                    "Scanner has no recent healthy observation or is blocked."
                )
            );

        if (!workerHealthy)
            alerts.Add(
                new("delivery_worker_stalled", "critical", "Delivery worker heartbeat is stale.")
            );

        if (!lagHealthy)
            alerts.Add(
                new(
                    "scanner_lag",
                    "warning",
                    "Scanner lag is unknown or exceeds its configured limit."
                )
            );

        if (!queueHealthy)
            alerts.Add(
                new(
                    "queue_backlog",
                    "warning",
                    "Outstanding delivery depth exceeds its configured limit."
                )
            );

        if (queue.ExpiredClaims > 0)
            alerts.Add(
                new("expired_claims", "warning", "Interrupted delivery claims await recovery.")
            );

        if (
            queue.RecentFailed >= options.DeliveryFailureMinimumAttempts
            && (double)queue.RecentFailed / Math.Max(1, queue.RecentFailed + queue.RecentSucceeded)
                >= options.DeliveryFailureRateThreshold
        )
            alerts.Add(
                new(
                    "sustained_delivery_failures",
                    "critical",
                    "Recent delivery failure rate exceeds its configured threshold."
                )
            );

        if (replays.Blocked > 0)
            alerts.Add(
                new("replay_blocked", "warning", "Historical replay requires explicit recovery.")
            );

        return new(
            alerts.Any(x => x.Severity == "critical") || !lagHealthy || !queueHealthy
                ? "degraded"
                : "ok",
            rpc.Network,
            scannerHealthy,
            workerHealthy,
            fresh,
            lagHealthy,
            queueHealthy,
            !string.IsNullOrWhiteSpace(rpc.FallbackEndpoint)
                && sample.ActiveRpcEndpoint == rpc.FallbackEndpoint,
            new(
                checkpoint?.NetworkTip ?? sample.NetworkTip,
                tip,
                next,
                scanned,
                lag,
                checkpoint?.LastSuccessfulScanAt,
                poll,
                publicError,
                sample.ScanBlocksPerSecond
            ),
            queue,
            replays,
            alerts,
            sample
        );
    }

    public static string PublicEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", "")
            : "configured RPC";
}
