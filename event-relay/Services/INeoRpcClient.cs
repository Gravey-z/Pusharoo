using System.Text.Json;
namespace Pusharoo.EventRelay.Services;
public interface INeoRpcClient
{
    Task ValidateNetworkAsync(CancellationToken cancellationToken);
    Task<uint> GetBlockCountAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetBlockTransactionsAsync(uint blockIndex, CancellationToken cancellationToken);
    Task<JsonElement> GetApplicationLogAsync(string transactionHash, CancellationToken cancellationToken);
}
