namespace Pusharoo.EventRelay.Models;

public enum PaymentVerificationStatus
{
    AwaitingFinality,
    Rejected,
    Verified,
}

public sealed record PaymentVerificationResult(
    PaymentVerificationStatus Status,
    uint BlockIndex = 0,
    long GasDatoshis = 0
);
