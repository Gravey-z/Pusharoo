using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

public sealed class NeoRpcClient(
    HttpClient httpClient,
    IOptions<NeoRpcOptions> options,
    RelayOperationsService operations,
    ILogger<NeoRpcClient> logger)
{
    private readonly NeoRpcOptions _options = options.Value;
    private int _requestId;

    public async Task<uint> GetBlockCountAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync("getblockcount", [], cancellationToken);
        return result.GetUInt32();
    }

    public async Task<IReadOnlyList<string>> GetBlockTransactionsAsync(uint blockIndex, CancellationToken cancellationToken)
    {
        var result = await SendAsync("getblock", [blockIndex, 1], cancellationToken);
        if (!result.TryGetProperty("tx", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hashes = new List<string>();
        foreach (var transaction in transactions.EnumerateArray())
        {
            if (transaction.ValueKind == JsonValueKind.String)
            {
                var hash = transaction.GetString();
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    hashes.Add(hash);
                }
            }
            else if (transaction.TryGetProperty("hash", out var hashElement))
            {
                var hash = hashElement.GetString();
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    hashes.Add(hash);
                }
            }
        }

        return hashes;
    }

    public async Task<JsonElement> GetApplicationLogAsync(string transactionHash, CancellationToken cancellationToken)
    {
        return await SendAsync("getapplicationlog", [transactionHash], cancellationToken);
    }

    public async Task<JsonElement> GetRawTransactionAsync(string transactionHash, CancellationToken cancellationToken)
    {
        return await SendAsync("getrawtransaction", [transactionHash, true], cancellationToken);
    }

    private async Task<JsonElement> SendAsync(
        string method,
        object?[] parameters,
        CancellationToken cancellationToken)
    {
        var request = new NeoRpcRequest
        {
            Method = method,
            Params = parameters,
            Id = Interlocked.Increment(ref _requestId)
        };

        try
        {
            return await SendToEndpointAsync(_options.Endpoint, request, method, cancellationToken);
        }
        catch (Exception ex) when (CanFailOver(ex, cancellationToken))
        {
            operations.RecordRpcFailure();
            var fallback = _options.FallbackEndpoint!.Trim();
            logger.LogWarning(ex,
                "Neo RPC {Method} failed on primary endpoint {PrimaryEndpoint}; trying fallback endpoint {FallbackEndpoint}.",
                method,
                _options.Endpoint,
                fallback);

            try
            {
                return await SendToEndpointAsync(fallback, request, method, cancellationToken);
            }
            catch
            {
                operations.RecordRpcFailure();
                throw;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            operations.RecordRpcFailure();
            throw;
        }
    }

    private async Task<JsonElement> SendToEndpointAsync(
        string endpoint,
        NeoRpcRequest request,
        string method,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(endpoint, request, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
        {
            throw new InvalidOperationException($"Neo RPC {method} failed: {error}");
        }

        if (!root.TryGetProperty("result", out var result))
        {
            throw new InvalidOperationException($"Neo RPC {method} returned no result.");
        }

        operations.RecordRpcSuccess(endpoint);
        return result.Clone();
    }

    private bool CanFailOver(Exception exception, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && !string.IsNullOrWhiteSpace(_options.FallbackEndpoint)
            && !string.Equals(_options.Endpoint.Trim(), _options.FallbackEndpoint.Trim(), StringComparison.OrdinalIgnoreCase)
            && exception is not OperationCanceledException;
    }

    private sealed class NeoRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; init; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; init; } = string.Empty;

        [JsonPropertyName("params")]
        public object?[] Params { get; init; } = [];

        [JsonPropertyName("id")]
        public int Id { get; init; }
    }
}
