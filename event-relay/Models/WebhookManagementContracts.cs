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

public sealed record CreatePaymentIntentRequest(WalletSignatureRequest? Signature);
public sealed record ConfirmPaymentRequest(string IntentId, string TransactionId, WalletSignatureRequest? Signature);
public sealed record PaymentIntentResponse(string Id, string ProjectId, string Network, string RecipientAddress, string RecipientScriptHash, long RequiredGasDatoshis, string Status, DateTime CreatedAt, DateTime ExpiresAt, string? ConfirmedTransactionId);
public sealed record PaymentResponse(string TransactionId, string IntentId, string Status, DateTime? EntitlementEndsAt, string? Message = null);
public sealed record PaymentHistoryResponse(IReadOnlyList<PaymentResponse> Payments, IReadOnlyList<EntitlementHistoryResponse> Entitlements);
public sealed record EntitlementHistoryResponse(string Network, string Plan, DateTime PeriodStart, DateTime PeriodEndsAt, DateTime GraceEndsAt);
