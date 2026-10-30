using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using Pusharoo.RetentionInventory;

namespace backend.Services;

public sealed class ProjectRetentionService(
    MongoDbContext db,
    TimeProvider clock,
    IOptions<UnusedProjectCleanupOptions> options
)
{
    public async Task<ProjectResponse> ToResponseAsync(
        ProjectDocument project,
        CancellationToken ct
    )
    {
        var result = await new InventoryReader(db.Projects.Database, clock).EvaluateAsync(
            project.Id,
            ct
        );
        var status = result?.Outcome switch
        {
            "protected_by_activity" => "protected",
            "waiting" => "unused",
            "eligible" => "awaiting_cleanup",
            "deleting" => "deleting",
            null => "unavailable",
            _ => "on_hold",
        };
        return project.ToResponse() with
        {
            Retention = new(
                status,
                options.Value.Mode,
                result?.DeadlineUtc,
                clock.GetUtcNow().UtcDateTime
            ),
        };
    }
}
