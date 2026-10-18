namespace Pusharoo.EventRelay.Models;

public sealed record ReplayAdmissionResult(
    bool Accepted,
    int StatusCode = 202,
    string? Error = null,
    DateTime? RetryAt = null
);

public sealed record ReplayAdmissionUsage(
    string JobId,
    string ProjectId,
    long Blocks,
    DateTime AdmittedAt,
    bool Active
);
