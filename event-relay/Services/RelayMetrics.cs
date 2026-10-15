using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Pusharoo.EventRelay.Services;

public static class RelayMetrics
{
    public static string Render(RelayHealthSnapshot health)
    {
        var text = new StringBuilder();
        var network = JsonSerializer.Serialize(health.Network);
        void Metric(string name, string kind, double? value)
        {
            if (value is null) return;
            text.Append("# TYPE ").Append(name).Append(' ').AppendLine(kind);
            text.Append(name).Append("{network=").Append(network).Append("} ")
                .AppendLine(value.Value.ToString("R", CultureInfo.InvariantCulture));
        }
        double? Unix(DateTime? time) => time is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000d : null;
        Metric("pusharoo_relay_scanner_healthy", "gauge", health.ScannerHealthy ? 1 : 0);
        Metric("pusharoo_relay_worker_healthy", "gauge", health.WorkerHealthy ? 1 : 0);
        Metric("pusharoo_relay_network_tip", "gauge", health.Scanner.NetworkTip);
        Metric("pusharoo_relay_confirmed_tip", "gauge", health.Scanner.ConfirmedTip);
        Metric("pusharoo_relay_scanned_height", "gauge", health.Scanner.ScannedHeight);
        Metric("pusharoo_relay_scanner_lag_blocks", "gauge", health.Scanner.LagBlocks);
        Metric("pusharoo_relay_last_successful_scan_timestamp_seconds", "gauge", Unix(health.Scanner.LastSuccessfulScanAt));
        Metric("pusharoo_relay_last_poll_timestamp_seconds", "gauge", Unix(health.Scanner.LastPollAt));
        Metric("pusharoo_relay_scanner_blocked", "gauge", health.Scanner.Error is null ? 0 : 1);
        Metric("pusharoo_relay_scanner_lag_healthy", "gauge", health.ScannerLagHealthy ? 1 : 0);
        Metric("pusharoo_relay_queue_healthy", "gauge", health.QueueHealthy ? 1 : 0);
        Metric("pusharoo_relay_sustained_delivery_failures", "gauge", health.Alerts.Any(x => x.Code == "sustained_delivery_failures") ? 1 : 0);
        Metric("pusharoo_relay_queue_depth", "gauge", health.Queue.Depth);
        Metric("pusharoo_relay_delivery_pending", "gauge", health.Queue.Pending);
        Metric("pusharoo_relay_delivery_retrying", "gauge", health.Queue.Retrying);
        Metric("pusharoo_relay_delivery_waiting", "gauge", health.Queue.Waiting);
        Metric("pusharoo_relay_delivery_inflight", "gauge", health.Queue.Delivering);
        Metric("pusharoo_relay_delivery_expired_claims", "gauge", health.Queue.ExpiredClaims);
        Metric("pusharoo_relay_delivery_dead_letters", "gauge", health.Queue.DeadLetters);
        Metric("pusharoo_relay_delivery_audit_pending", "gauge", health.Queue.AuditPending);
        Metric("pusharoo_relay_delivery_recent_succeeded", "gauge", health.Queue.RecentSucceeded);
        Metric("pusharoo_relay_delivery_recent_failed", "gauge", health.Queue.RecentFailed);
        Metric("pusharoo_relay_replay_running", "gauge", health.Replays.Running);
        Metric("pusharoo_relay_replay_paused", "gauge", health.Replays.Paused);
        Metric("pusharoo_relay_replay_blocked", "gauge", health.Replays.Blocked);
        Metric("pusharoo_relay_replay_completed_blocks", "gauge", health.Replays.CompletedBlocks);
        Metric("pusharoo_relay_replay_total_blocks", "gauge", health.Replays.TotalBlocks);
        Metric("pusharoo_relay_scan_blocks_total", "counter", health.Metrics.ScannedBlocks);
        Metric("pusharoo_relay_scan_transactions_total", "counter", health.Metrics.ScannedTransactions);
        Metric("pusharoo_relay_observed_events_total", "counter", health.Metrics.ObservedEvents);
        Metric("pusharoo_relay_scan_work_seconds_total", "counter", health.Metrics.ScanMilliseconds / 1000d);
        Metric("pusharoo_relay_rpc_requests_total", "counter", health.Metrics.RpcRequests);
        Metric("pusharoo_relay_rpc_failures_total", "counter", health.Metrics.RpcFailures);
        Metric("pusharoo_relay_delivery_succeeded_total", "counter", health.Metrics.Succeeded);
        Metric("pusharoo_relay_delivery_failed_total", "counter", health.Metrics.Failed);
        Metric("pusharoo_relay_delivery_retried_total", "counter", health.Metrics.Retried);
        Metric("pusharoo_relay_delivery_dead_letters_total", "counter", health.Metrics.DeadLetters);
        Metric("pusharoo_relay_delivery_latency_milliseconds", "gauge", health.Metrics.AverageDeliveryLatencyMilliseconds);
        return text.ToString();
    }
}
