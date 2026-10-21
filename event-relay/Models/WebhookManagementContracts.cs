namespace Pusharoo.EventRelay.Models;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(
    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
)]
public sealed record CreateRelayPaymentIntentRequest(
    string? PackageId = null,
    string Operation = "purchase_period",
    string[]? PeriodIds = null,
    string? RequestKey = null
);

public sealed record CreateSubscriptionRequest(
    string Name,
    string ContractHash,
    string Network,
    string? EventName,
    string WebhookUrl,
    string? Secret,
    Dictionary<string, string>? Headers,
    bool IsEnabled,
    int PayloadVersion = 2
);

public sealed record UpdateSubscriptionRequest(
    string Name,
    string ContractHash,
    string Network,
    string? EventName,
    string WebhookUrl,
    string? Secret,
    Dictionary<string, string>? Headers,
    bool IsEnabled,
    int PayloadVersion = 2
);

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
    WebhookDeliveryDocument? LatestDelivery,
    int PayloadVersion,
    string? PauseReason = null
);

public sealed record ConfirmPaymentRequest(string IntentId, string TransactionId);

public sealed record CancelPaymentIntentRequest(string IntentId);

public sealed record PaymentIntentResponse(
    string Id,
    string ProjectId,
    string Network,
    string RecipientAddress,
    string RecipientScriptHash,
    long RequiredGasDatoshis,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    string? ConfirmedTransactionId,
    string? SubmittedTransactionId,
    RelayPurchaseTerms? Terms = null,
    long UnappliedGasDatoshis = 0,
    string? RecoveryMessage = null
);

public sealed record PaymentResponse(
    string TransactionId,
    string IntentId,
    string Status,
    DateTime? EntitlementEndsAt,
    string? Message = null,
    RelayPurchaseTerms? Terms = null,
    long UnappliedGasDatoshis = 0,
    IReadOnlyList<RelayGrantAllocation>? Allocations = null,
    IReadOnlyList<RelayGrantProgressDocument>? GrantProgress = null
);

public sealed record PaymentHistoryResponse(
    IReadOnlyList<PaymentResponse> Payments,
    IReadOnlyList<EntitlementHistoryResponse> Entitlements,
    IReadOnlyList<PaymentIntentResponse> PendingIntents,
    IReadOnlyList<RelayPaymentResolutionDocument>? Resolutions = null,
    IReadOnlyList<RelayPeriodResponse>? Periods = null,
    IReadOnlyList<PaymentIntentResponse>? RecentIntents = null
);

public sealed record EntitlementHistoryResponse(
    string Network,
    string Plan,
    DateTime PeriodStart,
    DateTime PeriodEndsAt,
    DateTime GraceEndsAt,
    RelayPurchaseTerms? Terms = null,
    string? PeriodId = null,
    string? AllocationId = null
);

public sealed record SubscriptionUsageResponse(
    string Plan,
    string Status,
    DateTime PeriodStart,
    DateTime PeriodEndsAt,
    DateTime? GraceEndsAt,
    int MaxActiveSubscriptions,
    int ActiveSubscriptions,
    int MaxEvents,
    int EventsUsed,
    int EventsRemaining,
    string? PackageId = null,
    string? PurchasedPeriodId = null,
    RelayPackageTerms? PackageTerms = null,
    bool NeedsReconciliation = false,
    IReadOnlyList<RelayPeriodResponse>? ScheduledPeriods = null,
    RelayTransitionResponse? Transitions = null
);
