using System.Text.Json;
using Pusharoo.EventRelay.Models;

namespace Pusharoo.EventRelay.Services;

public interface INeoRpcClient
{
    Task ValidateNetworkAsync(CancellationToken cancellationToken);

    Task<uint> GetBlockCountAsync(CancellationToken cancellationToken);

    Task<NeoBlock> GetBlockAsync(uint blockIndex, CancellationToken cancellationToken);

    Task<JsonElement> GetApplicationLogAsync(
        string transactionHash,
        CancellationToken cancellationToken
    );
}
