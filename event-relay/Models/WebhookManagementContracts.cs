namespace Pusharoo.EventRelay.Models;

public sealed record WebhookAccessRequest(WalletSignatureRequest? Signature, string? SessionToken = null);

public sealed record CreateSubscriptionRequest(
    string Name,
    string ContractHash,
    string Network,
    string? EventName,
    string WebhookUrl,
    string? Secret,
    Dictionary<string, string>? Headers,
    bool IsEnabled,
    WalletSignatureRequest? Signature);

public sealed record UpdateSubscriptionRequest(
    string Name,
    string ContractHash,
    string Network,
    string? EventName,
    string WebhookUrl,
    string? Secret,
    Dictionary<string, string>? Headers,
    bool IsEnabled,
    WalletSignatureRequest? Signature);

public sealed record SubscriptionResponse(
    string Id,
    string ProjectId,
    string Name,
    string ContractHash,
    string Network,
    string? EventName,
    string WebhookUrl,
    Dictionary<string, string> Headers,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    WebhookDeliveryDocument? LatestDelivery);
