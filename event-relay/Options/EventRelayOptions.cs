namespace Pusharoo.EventRelay.Options;

public sealed class EventRelayOptions
{
    public const string SectionName = "EventRelay";

    public int WebhookTimeoutSeconds { get; init; } = 15;
    public int WebhookMaxAttempts { get; init; } = 4;
    public int WebhookRetryBaseSeconds { get; init; } = 2;
}
