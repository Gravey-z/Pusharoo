using backend.Models;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace backend.Services;

/// <summary>
/// Performs the non-destructive Stage 2 migration. It establishes explicit empty
/// collaboration collections for existing valid projects and marks legacy projects
/// whose creator cannot be verified as recovery-required/read-only.
/// </summary>
public sealed class ProjectOwnershipMigrationService(
    MongoDbContext db,
    ILogger<ProjectOwnershipMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<ProjectDocument>.Filter;
        var missingCreator = filter.Or(
            filter.Eq(project => project.CreatedByWalletAddress, null),
            filter.Eq(project => project.CreatedByWalletAddress, string.Empty),
            filter.Eq(project => project.CreatedByWalletScriptHash, null),
            filter.Eq(project => project.CreatedByWalletScriptHash, string.Empty),
            filter.Eq(project => project.CreatedByWalletPublicKey, null),
            filter.Eq(project => project.CreatedByWalletPublicKey, string.Empty));
        var recoveryRequired = await db.Projects.UpdateManyAsync(
            missingCreator,
            Builders<ProjectDocument>.Update
                .Set(project => project.OwnershipStatus, "recovery_required")
                .Set(project => project.OwnershipAuditedAtUtc, now),
            cancellationToken: cancellationToken);

        var verifiableCreator = filter.And(
            filter.Ne(project => project.CreatedByWalletAddress, null),
            filter.Ne(project => project.CreatedByWalletAddress, string.Empty),
            filter.Ne(project => project.CreatedByWalletScriptHash, null),
            filter.Ne(project => project.CreatedByWalletScriptHash, string.Empty),
            filter.Ne(project => project.CreatedByWalletPublicKey, null),
            filter.Ne(project => project.CreatedByWalletPublicKey, string.Empty));
        var missingCollaborators = filter.Or(
            filter.Exists("collaborators", false),
            filter.Eq(project => project.Collaborators, null));
        await db.Projects.UpdateManyAsync(
            filter.And(verifiableCreator, missingCollaborators),
            Builders<ProjectDocument>.Update.Set(project => project.Collaborators, []),
            cancellationToken: cancellationToken);
        await db.Projects.UpdateManyAsync(
            filter.And(verifiableCreator, filter.Or(
                filter.Exists("accessAuditEvents", false),
                filter.Eq(project => project.AccessAuditEvents, null))),
            Builders<ProjectDocument>.Update.Set(project => project.AccessAuditEvents, []),
            cancellationToken: cancellationToken);
        await db.Projects.UpdateManyAsync(
            verifiableCreator,
            Builders<ProjectDocument>.Update
                .Set(project => project.OwnershipStatus, "verified")
                .Set(project => project.OwnershipAuditedAtUtc, now),
            cancellationToken: cancellationToken);

        if (recoveryRequired.ModifiedCount > 0)
        {
            logger.LogWarning(
                "{Count} legacy Pusharoo project(s) have incomplete creator records and are now read-only until ownership recovery.",
                recoveryRequired.ModifiedCount);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
