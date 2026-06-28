using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Repositories;

public sealed class EventCheckpointRepository(MongoDbContext db) : IEventCheckpointRepository
{
    public async Task<EventCheckpointDocument?> GetAsync(string checkpointId, CancellationToken cancellationToken)
    {
        return await db.Checkpoints
            .Find(checkpoint => checkpoint.Id == checkpointId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task UpsertAsync(EventCheckpointDocument checkpoint, CancellationToken cancellationToken)
    {
        await db.Checkpoints.ReplaceOneAsync(
            item => item.Id == checkpoint.Id,
            checkpoint,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }
}
