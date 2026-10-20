using MongoDB.Bson.Serialization.Attributes;

namespace Pusharoo.EventRelay.Models;

public sealed record RelayPaymentIntentDocument
{
    [BsonElement("requestKey")] public string? RequestKey { get; init; }
    [BsonElement("requestFingerprint")] public string? RequestFingerprint { get; init; }
    [BsonElement("lateSubmission")] public bool LateSubmission { get; init; }
    [BsonElement("unappliedGasDatoshis")] public long UnappliedGasDatoshis { get; init; }
    [BsonElement("recoveryMessage")] public string? RecoveryMessage { get; init; }
    [BsonElement("applicationDecisionId")] public string? ApplicationDecisionId { get; init; }
    [BsonElement("activeResolutionId")] public string? ActiveResolutionId { get; init; }
    [BsonElement("completedResolutions")] public string[] CompletedResolutions { get; init; } = [];
    [BsonElement("confirmationBlocks")] public uint? ConfirmationBlocks { get; init; }
    [BsonElement("nextRecoveryAt")] public DateTime? NextRecoveryAt { get; init; }
    [BsonElement("terms")]
    public RelayPurchaseTerms? Terms { get; init; }

    [BsonId, BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [BsonElement("payerAddress")]
    public string PayerAddress { get; init; } = string.Empty;

    [BsonElement("payerScriptHash")]
    public string PayerScriptHash { get; init; } = string.Empty;

    [BsonElement("network")]
    public string Network { get; init; } = "neo3:mainnet";

    [BsonElement("recipientAddress")]
    public string RecipientAddress { get; init; } = string.Empty;

    [BsonElement("recipientScriptHash")]
    public string RecipientScriptHash { get; init; } = string.Empty;

    [BsonElement("requiredGasDatoshis")]
    public long RequiredGasDatoshis { get; init; }

    [BsonElement("status")]
    public string Status { get; init; } = "pending";

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }

    [BsonElement("expiresAt")]
    public DateTime ExpiresAt { get; init; }

    [BsonElement("confirmedTransactionId")]
    public string? ConfirmedTransactionId { get; init; }

    [BsonElement("submittedTransactionId")]
    public string? SubmittedTransactionId { get; init; }

    [BsonElement("submittedAt")]
    public DateTime? SubmittedAt { get; init; }

    [BsonElement("confirmedAt")]
    public DateTime? ConfirmedAt { get; init; }

    [BsonElement("rejectionReason")]
    public string? RejectionReason { get; init; }
}

public sealed class RelayPaymentDocument
{
    [BsonElement("terms")]
    public RelayPurchaseTerms? Terms { get; init; }

    [BsonId, BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("intentId")]
    public string IntentId { get; init; } = string.Empty;

    [BsonElement("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [BsonElement("senderScriptHash")]
    public string SenderScriptHash { get; init; } = string.Empty;

    [BsonElement("recipientScriptHash")]
    public string RecipientScriptHash { get; init; } = string.Empty;

    [BsonElement("gasDatoshis")]
    public long GasDatoshis { get; init; }

    [BsonElement("network")]
    public string Network { get; init; } = "neo3:mainnet";

    [BsonElement("blockIndex")]
    public uint BlockIndex { get; init; }

    [BsonElement("verifiedAt")]
    public DateTime VerifiedAt { get; init; }

    [BsonElement("entitlementEndsAt")]
    public DateTime? EntitlementEndsAt { get; init; }
}

public sealed class RelayEntitlementHistoryDocument
{
    [BsonElement("allocationId")] public string? AllocationId { get; init; }
    [BsonElement("terms")]
    public RelayPurchaseTerms? Terms { get; init; }

    [BsonElement("periodId")]
    public string? PeriodId { get; init; }

    [BsonId, BsonElement("_id")]
    public string Id { get; init; } = string.Empty;

    [BsonElement("paymentId")]
    public string PaymentId { get; init; } = string.Empty;

    [BsonElement("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [BsonElement("network")]
    public string Network { get; init; } = string.Empty;

    [BsonElement("plan")]
    public string Plan { get; init; } = "paid";

    [BsonElement("periodStart")]
    public DateTime PeriodStart { get; init; }

    [BsonElement("periodEndsAt")]
    public DateTime PeriodEndsAt { get; init; }

    [BsonElement("graceEndsAt")]
    public DateTime GraceEndsAt { get; init; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }
}
