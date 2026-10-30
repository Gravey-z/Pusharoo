using backend.Models;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class ProjectCleanupDiscovery(IMongoDatabase database)
{
    public async Task<IReadOnlyList<ProjectCleanupCandidate>> CandidatesAsync(
        DateTime now,
        int take,
        DateTime? afterCreatedAt,
        string? afterId,
        CancellationToken ct
    )
    {
        if (take is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(take));
        if ((afterCreatedAt is null) != (afterId is null))
            throw new ArgumentException("Both cursor fields are required.");
        var filter = Builders<ProjectDocument>.Filter.Where(project =>
            !project.IsDeleting
            && project.ContractSchemaVersion == 1
            && project.CreatedAt > DateTime.MinValue
            && project.CreatedAt
                <= now.AddDays(-Pusharoo.Retention.UnusedProjectRetentionPolicy.RetentionDays)
        );
        if (afterCreatedAt is { } date)
            filter &=
                Builders<ProjectDocument>.Filter.Gt(project => project.CreatedAt, date)
                | Builders<ProjectDocument>.Filter.Eq(project => project.CreatedAt, date)
                    & Builders<ProjectDocument>.Filter.Gt(project => project.Id, afterId);
        return await database
            .GetCollection<ProjectDocument>("projects")
            .Find(filter)
            .SortBy(project => project.CreatedAt)
            .ThenBy(project => project.Id)
            .Limit(take)
            .Project(project => new ProjectCleanupCandidate(project.Id, project.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectCleanupOperationSummary>> PendingPageAsync(
        int take,
        DateTime? afterCheckedAt,
        string? afterId,
        CancellationToken ct
    )
    {
        if (take is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(take));
        if ((afterCheckedAt is null) != (afterId is null))
            throw new ArgumentException("Both cursor fields are required.");
        var filter = Builders<ProjectCleanupDocument>.Filter.Where(operation =>
            operation.RunnerToken == null
            && (operation.Status == "prepared" || operation.Status == "admitted")
        );
        if (afterCheckedAt is { } date)
            filter &=
                Builders<ProjectCleanupDocument>.Filter.Gt(
                    operation => operation.CheckedAtUtc,
                    date
                )
                | Builders<ProjectCleanupDocument>.Filter.Eq(
                    operation => operation.CheckedAtUtc,
                    date
                ) & Builders<ProjectCleanupDocument>.Filter.Gt(operation => operation.Id, afterId);
        return await database
            .GetCollection<ProjectCleanupDocument>("projectCleanupOperations")
            .Find(filter)
            .SortBy(operation => operation.CheckedAtUtc)
            .ThenBy(operation => operation.Id)
            .Limit(take)
            .Project(operation => new ProjectCleanupOperationSummary(
                operation.Id,
                operation.OperationId,
                operation.CheckedAtUtc
            ))
            .ToListAsync(ct);
    }
}
