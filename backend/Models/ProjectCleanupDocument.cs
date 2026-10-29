using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed record ProjectCleanupDocument
{
    [BsonId]
    public string Id { get; init; } = string.Empty;

    [BsonElement("operationId")]
    public string OperationId { get; init; } = string.Empty;

    [BsonElement("reason")]
    public string Reason { get; init; } = Pusharoo.Retention.UnusedProjectRetentionPolicy.Reason;

    [BsonElement("policyVersion")]
    public string PolicyVersion { get; init; } =
        Pusharoo.Retention.UnusedProjectRetentionPolicy.Version;

    [BsonElement("status")]
    public string Status { get; init; } = "prepared";

    [BsonElement("createdAtUtc")]
    public DateTime CreatedAtUtc { get; init; }

    [BsonElement("deadlineUtc")]
    public DateTime DeadlineUtc { get; init; }

    [BsonElement("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; init; }

    [BsonElement("admittedAtUtc")]
    [BsonIgnoreIfNull]
    public DateTime? AdmittedAtUtc { get; init; }

    [BsonElement("completedAtUtc")]
    [BsonIgnoreIfNull]
    public DateTime? CompletedAtUtc { get; init; }

    [BsonElement("parentWriteToken")]
    public string ParentWriteToken { get; init; } = string.Empty;

    [BsonElement("admissionToken")]
    public string AdmissionToken { get; init; } = string.Empty;

    [BsonElement("runnerToken")]
    [BsonIgnoreIfNull]
    public string? RunnerToken { get; init; }

    [BsonElement("step")]
    public int Step { get; init; }

    [BsonElement("records")]
    public ProjectCleanupRecord[] Records { get; init; } = [];
}

public sealed record ProjectCleanupRecord
{
    [BsonElement("collection")]
    public string Collection { get; init; } = string.Empty;

    [BsonElement("id")]
    public BsonValue Id { get; init; } = BsonNull.Value;

    [BsonElement("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record ProjectCleanupResult(string Outcome, string? OperationId = null);

public sealed record ProjectCleanupCandidate(string Id, DateTime CreatedAtUtc);

public sealed record ProjectCleanupOperationSummary(
    string Id,
    string OperationId,
    DateTime CheckedAtUtc
);
