namespace Pusharoo.EventRelay.Models;

public sealed record WebhookTransportResult(
    bool Succeeded,
    bool Retryable,
    int? StatusCode,
    string? Error
);
