namespace Pusharoo.EventRelay.Options;

public sealed class EventRelayOptions
{
    public const string SectionName = "EventRelay";

    public int WebhookTimeoutSeconds { get; init; } = 15;
}
