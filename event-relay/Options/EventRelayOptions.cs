namespace Pusharoo.EventRelay.Options;

public sealed class EventRelayOptions
{
    public const string SectionName = "EventRelay";

    public int WebhookTimeoutSeconds { get; init; } = 15;
    public int WebhookMaxAttempts { get; init; } = 4;
    public int WebhookRetryBaseSeconds { get; init; } = 2;
    public int DeliveryPayloadRetentionDays { get; init; } = 7;
    public int DeliveryHistoryRetentionDays { get; init; } = 30;
    public int RetentionSweepMinutes { get; init; } = 60;
    public int TestnetSubscriptionRetentionDays { get; init; } = 7;
    public int ScannerStallSeconds { get; init; } = 120;
    public int DeliveryWorkerStallSeconds { get; init; } = 120;
    public int MaxScannerLagBlocks { get; init; } = 30;
    public int MaxQueueDepth { get; init; } = 1000;
}
