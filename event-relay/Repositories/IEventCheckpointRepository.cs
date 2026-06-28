using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Repositories;

public interface IEventCheckpointRepository
{
    Task<EventCheckpointDocument?> GetAsync(string checkpointId, CancellationToken cancellationToken);

    Task UpsertAsync(EventCheckpointDocument checkpoint, CancellationToken cancellationToken);
}
