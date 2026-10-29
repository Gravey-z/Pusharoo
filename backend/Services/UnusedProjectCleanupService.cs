using backend.Models;
using backend.Repositories;
using MongoDB.Driver;
using Pusharoo.Retention;
using Pusharoo.RetentionInventory;

namespace backend.Services;

public sealed class UnusedProjectCleanupService(
    MongoDbContext db,
    ContractWriteGuard contractWrites,
    ProjectCleanupRepository repository,
    TimeProvider clock,
    Func<string, Task>? checkpoint = null
)
{
    private ProjectWriteAdmission Admissions => new(db.Projects.Database);

    public async Task<ProjectCleanupResult> TryCleanupAsync(string projectId, CancellationToken ct)
    {
        var prior = await repository.GetAsync(projectId, ct);
        if (prior is not null)
            ValidateOperation(prior);
        if (prior?.Status == "completed")
            return new("completed", prior.OperationId);
        if (prior is { Status: "prepared" or "admitted" })
        {
            var claimed = await repository.ClaimAsync(projectId, Guid.NewGuid().ToString("N"), ct);
            return claimed is null
                ? new("busy", prior.OperationId)
                : await ExecuteAsync(claimed, ct);
        }
        var observed = await new InventoryReader(db.Projects.Database, clock).EvaluateAsync(
            projectId,
            ct
        );
        if (observed is null)
            return new("missing");
        if (observed.Outcome == "protected_by_activity")
            return new("protected_by_activity");
        if (observed.Outcome is "waiting" or "needs_reconciliation")
            return new(observed.Outcome);
        string parentToken;
        try
        {
            parentToken = await contractWrites.AcquireAsync(projectId, ct);
        }
        catch (ContractOperationException error) when (error.StatusCode == 409)
        {
            return new("busy");
        }
        var admissionToken = Guid.NewGuid().ToString("N");
        var excluded = false;
        ProjectCleanupDocument? operation = null;
        try
        {
            excluded = await Admissions.TryExcludeWritersAsync(projectId, admissionToken, ct);
            if (!excluded)
                return new("busy");
            var operationId = prior?.OperationId ?? Guid.NewGuid().ToString("N");
            var inventory = await new InventoryReader(db.Projects.Database, clock).EvaluateAsync(
                projectId,
                ct,
                parentToken,
                admissionToken,
                operationId
            );
            if (inventory is null || inventory.Outcome != "eligible")
                return new(inventory?.Outcome ?? "missing");
            var manifest = await repository.FreezeRecordsAsync(projectId, ct);
            operation = new()
            {
                Id = projectId,
                OperationId = operationId,
                CreatedAtUtc = inventory.CreatedAtUtc!.Value,
                DeadlineUtc = inventory.DeadlineUtc!.Value,
                CheckedAtUtc = clock.GetUtcNow().UtcDateTime,
                ParentWriteToken = parentToken,
                AdmissionToken = admissionToken,
                RunnerToken = Guid.NewGuid().ToString("N"),
                Records = manifest,
            };
            if (!await repository.PrepareAsync(operation, ct))
            {
                operation = null;
                return new("busy");
            }
            await CheckpointAsync("prepared");
            if (
                !await repository.AdmitAsync(
                    operation,
                    clock.GetUtcNow().UtcDateTime,
                    ReferenceEquals(clock, TimeProvider.System),
                    ct
                )
            )
                throw new IOException("Project cleanup admission changed.");
            return await RunAdmittedAsync(operation, ct);
        }
        finally
        {
            if (operation is null)
            {
                if (!await repository.AbortAdmissionAsync(projectId, parentToken))
                    throw new IOException(
                        "Cleanup preflight ownership is uncertain; retain admission for reconciliation."
                    );
                if (excluded)
                    await Admissions.ReleaseExclusionAsync(projectId, admissionToken);
            }
            else
                await SettleAsync(operation);
        }
    }

    private async Task<ProjectCleanupResult> ExecuteAsync(
        ProjectCleanupDocument operation,
        CancellationToken ct
    )
    {
        try
        {
            await contractWrites.EnsureMigrationAllowsWritesAsync(ct);
            var project = await db
                .Projects.Find(item => item.Id == operation.Id)
                .FirstOrDefaultAsync(ct);
            if (
                project is not null
                && (!project.IsDeleting || project.CleanupOperationId != operation.OperationId)
            )
                throw new IOException(
                    "Stored cleanup intent does not match the project deletion marker."
                );
            if (
                project is null
                && operation.Step < ProjectCleanupRepository.CascadeCollections.Count
            )
                throw new IOException("Project disappeared before the admitted cascade completed.");
            return await RunAdmittedAsync(operation, ct);
        }
        finally
        {
            await SettleAsync(operation);
        }
    }

    private async Task<ProjectCleanupResult> RunAdmittedAsync(
        ProjectCleanupDocument operation,
        CancellationToken ct
    )
    {
        await contractWrites.EnsureMigrationAllowsWritesAsync(ct);
        await Admissions.RetireAsync(operation.Id, operation.AdmissionToken, ct);
        await repository.RecordAdmissionAsync(operation, clock.GetUtcNow().UtcDateTime, ct);
        await repository.ProgressAsync(
            operation,
            "admitted",
            operation.Step,
            clock.GetUtcNow().UtcDateTime,
            ct
        );
        await CheckpointAsync("admitted");
        for (
            var step = operation.Step;
            step < ProjectCleanupRepository.CascadeCollections.Count;
            step++
        )
        {
            var collection = ProjectCleanupRepository.CascadeCollections[step];
            await repository.DeleteManifestAsync(operation, collection, ct);
            await repository.ProgressAsync(
                operation,
                "admitted",
                step + 1,
                clock.GetUtcNow().UtcDateTime,
                ct
            );
            await CheckpointAsync("collection:" + collection);
        }
        await repository.VerifyEmptyAsync(operation, ct);
        var deleted = await repository.DeleteParentAsync(operation.Id, operation.OperationId, ct);
        if (
            deleted.DeletedCount == 0
            && await db.Projects.Find(project => project.Id == operation.Id).AnyAsync(ct)
        )
            throw new IOException("The project deletion marker changed before parent removal.");
        await CheckpointAsync("parent-deleted");
        await repository.ProgressAsync(
            operation,
            "completed",
            ProjectCleanupRepository.CascadeCollections.Count,
            clock.GetUtcNow().UtcDateTime,
            ct
        );
        await CheckpointAsync("completed");
        return new("completed", operation.OperationId);
    }

    private async Task SettleAsync(ProjectCleanupDocument operation)
    {
        if (await repository.AbortAdmissionAsync(operation.Id, operation.ParentWriteToken))
        {
            await Admissions.ReleaseExclusionAsync(operation.Id, operation.AdmissionToken);
            await repository.ProgressAsync(
                operation,
                "deferred",
                0,
                clock.GetUtcNow().UtcDateTime,
                CancellationToken.None
            );
        }
        await repository.ReleaseRunnerAsync(operation);
    }

    private Task CheckpointAsync(string stage) => checkpoint?.Invoke(stage) ?? Task.CompletedTask;

    private static void ValidateOperation(ProjectCleanupDocument operation)
    {
        if (
            operation.Reason != UnusedProjectRetentionPolicy.Reason
            || operation.PolicyVersion != UnusedProjectRetentionPolicy.Version
            || !Guid.TryParseExact(operation.OperationId, "N", out _)
            || !Guid.TryParseExact(operation.AdmissionToken, "N", out _)
            || !Guid.TryParseExact(operation.ParentWriteToken, "N", out _)
            || operation.CreatedAtUtc == default
            || operation.DeadlineUtc <= operation.CreatedAtUtc
            || operation.DeadlineUtc - operation.CreatedAtUtc
                != TimeSpan.FromDays(UnusedProjectRetentionPolicy.RetentionDays)
            || operation.Status is not ("prepared" or "admitted" or "deferred" or "completed")
            || operation.Step < 0
            || operation.Step > ProjectCleanupRepository.CascadeCollections.Count
            || operation.Status is "prepared" or "deferred" && operation.Step != 0
            || operation.Status is "admitted" or "completed" && operation.AdmittedAtUtc is null
            || operation.Status == "completed"
                && (
                    operation.CompletedAtUtc is null
                    || operation.Step != ProjectCleanupRepository.CascadeCollections.Count
                )
            || operation.Records.Length > 5000
            || operation.Records.Any(record =>
                !ProjectCleanupRepository.CascadeCollections.Contains(record.Collection)
                || record.Id.IsBsonNull
                || record.Sha256.Length != 64
                || !record.Sha256.All(Uri.IsHexDigit)
            )
        )
            throw new IOException(
                "Cleanup journal requires explicit reconciliation before it can resume."
            );
    }
}
