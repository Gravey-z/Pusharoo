namespace Pusharoo.RetentionInventory;

public sealed record InventoryReport(
    DateTime AsOfUtc,
    IReadOnlyList<ProjectInventory> Projects,
    string? NextAfterProjectId
)
{
    public string PolicyVersion => "unused-project-v1";
    public int RetentionDays => 30;
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
