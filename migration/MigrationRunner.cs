using MongoDB.Bson;
using MongoDB.Driver;
using static Pusharoo.Migration.MigrationIdentity;

namespace Pusharoo.Migration;

public sealed class MigrationRunner(MigrationRepository repository, MigrationPlanner planner)
{
    public async Task<MigrationReport> InventoryAsync(
        MigrationDecisions? decisions,
        CancellationToken ct
    ) => planner.Build(await repository.ReadAsync(ct), decisions);

    public async Task<MigrationResult> ApplyAsync(
        MigrationDecisions? decisions,
        MigrationApplyOptions options,
        CancellationToken ct,
        Func<string, Task>? checkpoint = null
    )
    {
        if (!options.WritersStopped || !options.BackupVerified || !options.WritesDisabled)
            throw new MigrationBlockedException(
                "Apply requires stopped writers, a verified backup, and disabled workspace writes."
            );
        var token = await repository.AcquireAsync(ct);
        var status = "blocked";
        try
        {
            var report = await InventoryAsync(decisions, ct);
            if (!report.ReadyToApply)
            {
                foreach (
                    var project in report.Projects.Where(project =>
                        report.Issues.Any(issue => issue.ProjectId == project.Id)
                    )
                )
                    await repository.BlockProjectAsync(project.Id, ct);
                return new("blocked", report);
            }
            foreach (var plan in report.Plans)
            {
                await repository.AssertOwnerAsync(token, ct);
                await repository.SavePlanAsync(plan, ct);
                await repository.BlockProjectAsync(plan.ProjectId, ct);
            }
            if (checkpoint is not null)
                await checkpoint("mapping");
            foreach (var plan in report.Plans)
            {
                foreach (var contract in plan.Contracts)
                {
                    await repository.AssertOwnerAsync(token, ct);
                    await repository.EnsureContractAsync(contract, ct);
                }
                foreach (var record in plan.Records)
                {
                    await repository.AssertOwnerAsync(token, ct);
                    await repository.BackfillAsync(record, ct);
                    if (checkpoint is not null)
                        await checkpoint("record:" + record.Kind + ":" + record.Id);
                }
                await repository.SetProgressAsync(plan.ProjectId, "backfilled", ct);
            }
            await VerifyBackfillAsync(report.Plans, ct);
            if (checkpoint is not null)
                await checkpoint("backfill");
            await repository.AssertOwnerAsync(token, ct);
            await repository.CreateIndexesAsync(ct);
            await VerifyIndexesAsync(false, ct);
            if (checkpoint is not null)
                await checkpoint("indexes-built");
            await VerifyBackfillAsync(report.Plans, ct);
            await repository.AssertOwnerAsync(token, ct);
            await repository.DropLegacyArtifactIndexesAsync(ct);
            await VerifyIndexesAsync(true, ct);
            if (checkpoint is not null)
                await checkpoint("indexes-replaced");
            foreach (var plan in report.Plans)
            {
                await repository.AssertOwnerAsync(token, ct);
                await repository.CompleteProjectAsync(plan, ct);
            }
            var final = await InventoryAsync(null, ct);
            if (!final.ReadyToApply)
                throw new MigrationBlockedException(
                    "Final migration verification failed. Keep writes disabled."
                );
            status = "ready";
            return new(status, final);
        }
        finally
        {
            await repository.ReleaseAsync(token, status);
        }
    }

    public async Task VerifyBackfillAsync(IReadOnlyList<ProjectPlan> plans, CancellationToken ct)
    {
        var snapshot = await repository.ReadAsync(ct);
        var report = planner.Build(snapshot);
        if (!report.ReadyToApply)
            throw new MigrationBlockedException(
                "Source integrity, lineage, or unresolved-attempt checks failed after backfill."
            );
        foreach (var plan in plans)
        {
            var currentPlan = report.Plans.SingleOrDefault(item =>
                item.ProjectId == plan.ProjectId
            );
            if (
                currentPlan is null
                || currentPlan.SourceHash != plan.SourceHash
                || currentPlan.Records.Count != plan.Records.Count
            )
                throw new MigrationBlockedException(
                    "Project counts or identity changed during backfill."
                );
            foreach (var record in plan.Records)
            {
                var current = (
                    record.Kind == "artifact" ? snapshot.Artifacts : snapshot.Deployments
                ).SingleOrDefault(item => Id(item) == record.Id);
                if (
                    current is null
                    || Parent(current) != record.ProjectId
                    || Text(current, "contractId") != record.ContractId
                    || Hash(current) != record.SourceHash
                    || (
                        record.AuditLegacy
                        && !current.GetValue("legacyContractMappingVerified", false).ToBoolean()
                    )
                )
                    throw new MigrationBlockedException(
                        "Backfill did not preserve original evidence and parent identities."
                    );
            }
        }
    }

    public async Task VerifyIndexesAsync(bool requireLegacyAbsent, CancellationToken ct)
    {
        var contracts = await repository.ReadIndexesAsync("contracts", ct);
        if (
            !contracts.Any(item =>
                item["key"].Equals(new BsonDocument { { "projectId", 1 }, { "normalizedName", 1 } })
                && FullUnique(item, false)
            )
        )
            throw new MigrationBlockedException("Contract name uniqueness is not verified.");
        var artifacts = await repository.ReadIndexesAsync("contractArtifacts", ct);
        var scoped = new BsonDocument
        {
            { "projectId", 1 },
            { "contractId", 1 },
            { "version", 1 },
        };
        if (!artifacts.Any(item => item["key"].Equals(scoped) && FullUnique(item, false)))
            throw new MigrationBlockedException(
                "The full scoped artifact uniqueness index is not verified."
            );
        if (
            requireLegacyAbsent
            && artifacts.Any(item =>
                item.GetValue("unique", false).ToBoolean()
                && item["key"].AsBsonDocument.Names.ToHashSet().SetEquals(["projectId", "version"])
            )
        )
            throw new MigrationBlockedException(
                "A legacy project/version uniqueness index remains."
            );
        var deployments = await repository.ReadIndexesAsync("deployments", ct);
        foreach (var field in new[] { "transactionId", "activeAttemptKey" })
            if (
                !deployments.Any(item =>
                    item["key"].Equals(new BsonDocument(field, 1)) && FullUnique(item, true)
                )
            )
                throw new MigrationBlockedException(
                    "Transaction or attempt uniqueness is not verified."
                );
    }

    private static bool FullUnique(BsonDocument index, bool sparse) =>
        index.GetValue("unique", false).ToBoolean()
        && index.GetValue("sparse", false).ToBoolean() == sparse
        && !index.Contains("partialFilterExpression")
        && !index.Contains("collation");
}
