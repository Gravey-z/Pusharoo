namespace Pusharoo.RetentionInventory;

public sealed record InventoryReport(
    DateTime AsOfUtc,
    IReadOnlyList<ProjectInventory> Projects,
    string? NextAfterProjectId
)
{
    public string PolicyVersion => Pusharoo.Retention.UnusedProjectRetentionPolicy.Version;
    public int RetentionDays => Pusharoo.Retention.UnusedProjectRetentionPolicy.RetentionDays;
    public bool DiagnosticOnly => true;
    public bool DeletionAuthorized => false;
}

public sealed record ProjectInventory(
    string ProjectId,
    DateTime? CreatedAtUtc,
    DateTime? DeadlineUtc,
    string Outcome,
    IReadOnlyList<string> Reasons,
    IReadOnlyDictionary<string, int> ObservedRecords
);
