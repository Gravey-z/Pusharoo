using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace backend.Services;

public sealed class NeoRpcClient(HttpClient httpClient)
{
    private int requestId;

    public async Task<JsonElement> SendAsync(
        string endpoint,
        string method,
        object?[] parameters,
        CancellationToken cancellationToken)
    {
        var request = new NeoRpcRequest
        {
            Method = method,
            Params = parameters,
            Id = Interlocked.Increment(ref requestId)
        };

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

        return result.Clone();
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
