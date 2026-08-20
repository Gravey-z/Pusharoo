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
    public int FreeBetaPeriodDays { get; init; } = 30;
    public int FreeTestnetMaxActiveSubscriptions { get; init; } = 5;
    public int FreeTestnetMaxEvents { get; init; } = 10000;
    public string PaymentRecipientAddress { get; init; } = string.Empty;
    public string PaymentRecipientScriptHash { get; init; } = string.Empty;
    public long PaidPlanGasDatoshis { get; init; } = 500000000;
    public int PaidPlanDays { get; init; } = 30;
    public int PaidGraceDays { get; init; } = 3;
    public int PaidMaxActiveSubscriptions { get; init; } = 5;
    public int PaidMaxEvents { get; init; } = 10000;
    public int PaymentIntentMinutes { get; init; } = 15;
    public uint PaymentConfirmationBlocks { get; init; } = 2;
}
