using backend.Models;
using backend.Options;
using backend.Repositories;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.RetentionInventory;

namespace backend.Services;

public sealed class UnusedProjectCleanupWorker(
    IServiceScopeFactory scopes,
    IOptions<UnusedProjectCleanupOptions> configuration,
    TimeProvider clock,
    ILogger<UnusedProjectCleanupWorker> logger
) : BackgroundService
{
    private const int PageSize = 100;

    private readonly UnusedProjectCleanupOptions options = configuration.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Unused-project cleanup mode {Mode}.", options.Mode);
        if (options.Mode == "disabled")
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(
                    "Unused-project cleanup sweep failed with {ErrorType}; retry in {RetrySeconds}s. Check database availability and cleanup configuration.",
                    error.GetType().Name,
                    delay.TotalSeconds
                );
            }
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<UnusedProjectCleanupBatch> RunOnceAsync(CancellationToken ct)
    {
        var batch = new UnusedProjectCleanupBatch();
        if (options.Mode == "disabled")
            return batch;
        await using var scope = scopes.CreateAsyncScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IOptions<MongoDbOptions>>().Value;
        var database = new MongoClient(mongo.ConnectionString).GetDatabase(mongo.DatabaseName);
        var repository = new ProjectCleanupDiscovery(database);
        var reader = new InventoryReader(database, clock);
        var cleanup =
            options.Mode == "enabled"
                ? scope.ServiceProvider.GetRequiredService<UnusedProjectCleanupService>()
                : null;
        DateTime? pendingDate = null;
        string? pendingId = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var pending = await repository.PendingPageAsync(PageSize, pendingDate, pendingId, ct);
            foreach (var operation in pending)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(operation.Id);
                await ProcessAsync(operation.Id, operation.OperationId, true);
                pendingDate = operation.CheckedAtUtc;
                pendingId = operation.Id;
            }
            if (pending.Count < PageSize)
                break;
        }
        DateTime? candidateDate = null;
        string? candidateId = null;
        var checkedAt = clock.GetUtcNow().UtcDateTime;
        while (true)
        {
            var candidates = await repository.CandidatesAsync(
                checkedAt,
                PageSize,
                candidateDate,
                candidateId,
                ct
            );
            foreach (var project in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Contains(project.Id))
                    await ProcessAsync(project.Id, null, false);
                candidateDate = project.CreatedAtUtc;
                candidateId = project.Id;
            }
            if (candidates.Count < PageSize)
                break;
        }
        logger.LogInformation(
            "Unused-project cleanup {Mode}: scanned {Scanned}, protected {Protected}, blocked {Blocked}, eligible {Eligible}, deleted {Deleted}, failed {Failed}, pending {Pending}. Dry-run results are snapshots requiring guarded revalidation.",
            options.Mode,
            batch.Scanned,
            batch.Protected,
            batch.Blocked,
            batch.Eligible,
            batch.Deleted,
            batch.Failed,
            batch.Pending
        );
        return batch;

        async Task ProcessAsync(string projectId, string? operationId, bool recovering)
        {
            batch.Scanned++;
            if (recovering)
                batch.Pending++;
            try
            {
                var result = cleanup is not null
                    ? await cleanup.TryCleanupAsync(projectId, ct)
                    : new ProjectCleanupResult(
                        (await reader.EvaluateAsync(projectId, ct))?.Outcome ?? "missing",
                        operationId
                    );
                switch (result.Outcome)
                {
                    case "completed":
                        batch.Deleted++;
                        break;
                    case "protected_by_activity":
                        batch.Protected++;
                        break;
                    case "eligible":
                        batch.Eligible++;
                        break;
                    default:
                        batch.Blocked++;
                        break;
                }
                logger.Log(
                    result.Outcome is "completed" or "eligible"
                        ? LogLevel.Information
                        : LogLevel.Debug,
                    "Unused-project cleanup {Mode}: project {ProjectId}, operation {OperationId}, outcome {Outcome}.",
                    options.Mode,
                    projectId,
                    result.OperationId,
                    result.Outcome
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                batch.Failed++;
                logger.LogError(
                    "Unused-project cleanup project {ProjectId}, operation {OperationId}, phase {Phase} failed with {ErrorType}; retry on the next scheduled sweep. Inspect the cleanup journal and exact-token recovery runbook; do not clear guards by age.",
                    projectId,
                    operationId,
                    recovering ? "resume" : "candidate",
                    error.GetType().Name
                );
            }
        }
    }
}
