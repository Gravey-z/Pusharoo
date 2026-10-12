using Pusharoo.EventRelay.Models;
namespace Pusharoo.EventRelay.Repositories;
public interface IEventCheckpointRepository
{
    Task<EventCheckpointDocument?> GetAsync(string checkpointId, CancellationToken cancellationToken);
    Task<EventCheckpointDocument?> TryAcquireAsync(string checkpointId, string owner, TimeSpan duration, CancellationToken cancellationToken);
    Task<bool> RenewAsync(string checkpointId, string owner, TimeSpan duration, CancellationToken cancellationToken);
    Task<bool> AdvanceAsync(string checkpointId, string owner, uint? expectedNextBlock, uint nextBlock, CancellationToken cancellationToken);
    Task<bool> BlockAsync(string checkpointId, string owner, ScannerBlockingError error, CancellationToken cancellationToken);
    Task ReleaseAsync(string checkpointId, string owner, CancellationToken cancellationToken);
}
