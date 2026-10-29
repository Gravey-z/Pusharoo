using MongoDB.Bson;

namespace Pusharoo.RetentionInventory;

internal static class InventoryClassifier
{
    internal static ProjectInventory Classify(
        BsonDocument project,
        DateTime now,
        bool hasActivity,
        SortedSet<string> reconciliation,
        SortedSet<string> blockers,
        SortedDictionary<string, int> observations
    )
    {
        DateTime? created = null;
        DateTime? deadline = null;
        if (project.GetValue("createdAt", BsonNull.Value) is BsonDateTime date)
        {
            try
            {
                created = date.ToUniversalTime();
                deadline = created.Value.AddDays(30);
                if (created == DateTime.MinValue || created > now)
                    reconciliation.Add("invalid_creation_time");
            }
            catch (ArgumentOutOfRangeException)
            {
                reconciliation.Add("invalid_creation_time");
            }
        }
        else
            reconciliation.Add("invalid_creation_time");

        if (project.GetValue("contractSchemaVersion", BsonNull.Value) != new BsonInt32(1))
            reconciliation.Add("unsupported_project_schema");
        if (
            project.Contains("contractMigrationState")
            && project["contractMigrationState"] != "ready"
        )
            blockers.Add("project_migration_state");
        var deletion = project.GetValue("isDeleting", false);
        if (!deletion.IsBoolean)
            reconciliation.Add("invalid_deletion_state");
        if (!project.GetValue("contractWriteToken", BsonNull.Value).IsBsonNull)
            blockers.Add("project_writer");

        var outcome =
            deletion.IsBoolean && deletion.AsBoolean ? "deleting"
            : hasActivity ? "protected_by_activity"
            : reconciliation.Count > 0 ? "needs_reconciliation"
            : blockers.Count > 0 ? "blocked_by_dependencies"
            : deadline > now ? "waiting"
            : "eligible";
        var reasons = reconciliation.Concat(blockers).ToList();
        if (hasActivity)
            reasons.Insert(0, "recorded_activity");
        if (outcome == "deleting")
            reasons.Insert(0, "existing_deletion_requires_reconciliation");
        if (outcome is "waiting" or "eligible")
            reasons.Add(
                outcome == "waiting" ? "before_creation_deadline" : "creation_deadline_reached"
            );

        return new(project["_id"].AsString, created, deadline, outcome, reasons, observations);
    }
}
