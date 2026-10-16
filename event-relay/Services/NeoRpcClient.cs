using Pusharoo.EventRelay.Models;
using System.Collections.Concurrent;
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
    ILogger<NeoRpcClient> logger,
    NeoRpcRequestLimiter? limiter = null
) : INeoRpcClient
{
    private readonly NeoRpcOptions settings = options.Value;

    private readonly ConcurrentDictionary<string, DateTime> validated = new(
        StringComparer.OrdinalIgnoreCase
    );

    private int requestId;

    private string[] Endpoints =>
        new[] { settings.Endpoint, settings.FallbackEndpoint }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task ValidateNetworkAsync(CancellationToken cancellationToken)
    {
        var failures = new List<NeoRpcException>();
        var available = false;

        foreach (var endpoint in Endpoints)
        {
            try
            {
                await ValidateEndpointAsync(endpoint, cancellationToken);
                available = true;
            }
            catch (NeoRpcException ex) when (ex.Kind != NeoRpcFailureKind.NetworkMismatch)
            {
                failures.Add(ex);
            }
        }

        if (!available)
            throw Combined("getversion", failures);
    }

    private async Task ValidateEndpointAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (
            validated.TryGetValue(endpoint, out var at)
            && DateTime.UtcNow - at < TimeSpan.FromSeconds(60)
        )
            return;

        await RetryEndpointAsync(
            endpoint,
            "getversion",
            [],
            result =>
            {
                var protocol = NeoApplicationLogParser.RequiredObject(result, "protocol");

                if (
                    !protocol.TryGetProperty("network", out var magic)
                    || magic.ValueKind != JsonValueKind.Number
                    || !magic.TryGetUInt32(out var actual)
                )
                    throw new FormatException("Missing or invalid protocol.network.");

                if (actual != settings.NetworkMagic)
                    throw new NeoRpcException(
                        endpoint,
                        "getversion",
                        NeoRpcFailureKind.NetworkMismatch,
                        $"Network magic {actual} does not match {settings.Network} ({settings.NetworkMagic})."
                    );

                return true;
            },
            cancellationToken
        );
        validated[endpoint] = DateTime.UtcNow;
    }

    public Task<uint> GetBlockCountAsync(CancellationToken cancellationToken) =>
        SendAsync(
            "getblockcount",
            [],
            result =>
            {
                if (
                    result.ValueKind != JsonValueKind.Number
                    || !result.TryGetUInt32(out var count)
                    || count == 0
                )
                    throw new FormatException("Invalid block count.");

                return count;
            },
            cancellationToken
        );

    public async Task<IReadOnlyList<string>> GetBlockTransactionsAsync(
        uint blockIndex,
        CancellationToken cancellationToken
    ) => (await GetBlockAsync(blockIndex, cancellationToken)).TransactionHashes;

    public Task<NeoBlock> GetBlockAsync(uint blockIndex, CancellationToken cancellationToken) =>
        SendAsync<NeoBlock>(
            "getblock",
            [blockIndex, 1],
            result =>
            {
                if (
                    result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("index", out var index)
                    || index.ValueKind != JsonValueKind.Number
                    || !index.TryGetUInt32(out var actual)
                    || actual != blockIndex
                )
                    throw new FormatException(
                        "Block index is missing or does not match the requested block."
                    );

                var transactions = NeoApplicationLogParser.RequiredArray(result, "tx");
                var hashes = new List<string>();

                foreach (var tx in transactions.EnumerateArray())
                {
                    var hash =
                        tx.ValueKind == JsonValueKind.String
                            ? tx.GetString()!
                            : NeoApplicationLogParser.RequiredString(tx, "hash");

                    if (!NeoApplicationLogParser.IsHash(hash, 32))
                        throw new FormatException("Invalid transaction hash.");

                    hashes.Add(hash.ToLowerInvariant());
                }

                if (hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Count)
                    throw new FormatException("Duplicate transaction hashes in block.");

                var blockHash = NeoApplicationLogParser.RequiredString(result, "hash");

                if (!NeoApplicationLogParser.IsHash(blockHash, 32))
                    throw new FormatException("Invalid block hash.");

                if (
                    !result.TryGetProperty("time", out var time)
                    || time.ValueKind != JsonValueKind.Number
                    || !time.TryGetInt64(out var milliseconds)
                    || milliseconds < 0
                )
                    throw new FormatException(
                        "Missing or invalid block timestamp (Unix milliseconds)."
                    );

                DateTimeOffset timestamp;

                try
                {
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw new FormatException(
                        "Block timestamp is outside the supported range.",
                        ex
                    );
                }

                return new NeoBlock(blockIndex, blockHash.ToLowerInvariant(), timestamp, hashes);
            },
            cancellationToken
        );

    public Task<JsonElement> GetApplicationLogAsync(
        string transactionHash,
        CancellationToken cancellationToken
    ) =>
        SendAsync(
            "getapplicationlog",
            [transactionHash],
            result =>
            {
                NeoApplicationLogParser.Validate(result, transactionHash);

                return result.Clone();
            },
            cancellationToken
        );

    public Task<JsonElement> GetRawTransactionAsync(
        string transactionHash,
        CancellationToken cancellationToken
    ) =>
        SendAsync(
            "getrawtransaction",
            [transactionHash, true],
            result =>
            {
                if (
                    !string.Equals(
                        NeoApplicationLogParser.RequiredString(result, "hash"),
                        transactionHash,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    throw new FormatException("Raw transaction hash does not match the request.");

                return result.Clone();
            },
            cancellationToken
        );

    private async Task<T> SendAsync<T>(
        string method,
        object?[] parameters,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken
    )
    {
        await ValidateNetworkAsync(cancellationToken);
        var failures = new List<NeoRpcException>();

        foreach (var endpoint in Endpoints)
        {
            try
            {
                await ValidateEndpointAsync(endpoint, cancellationToken);

                return await RetryEndpointAsync(
                    endpoint,
                    method,
                    parameters,
                    parse,
                    cancellationToken
                );
            }
            catch (NeoRpcException ex) when (ex.Kind != NeoRpcFailureKind.NetworkMismatch)
            {
                failures.Add(ex);
                logger.LogWarning(
                    ex,
                    "Neo RPC {Method} exhausted attempts at {Endpoint}; trying a configured fallback if available.",
                    method,
                    endpoint
                );
            }
        }

        throw Combined(method, failures);
    }

    private static NeoRpcException Combined(string method, List<NeoRpcException> errors)
    {
        var kind =
            errors.Any(x => x.Kind == NeoRpcFailureKind.Transient) ? NeoRpcFailureKind.Transient
            : errors.Any(x => x.Kind == NeoRpcFailureKind.InvalidResponse)
                ? NeoRpcFailureKind.InvalidResponse
            : NeoRpcFailureKind.UnavailableHistory;

        return new NeoRpcException(
            string.Join(", ", errors.Select(x => x.Endpoint)),
            method,
            kind,
            string.Join("; ", errors.Select(x => x.Message)),
            errors.LastOrDefault()
        );
    }

    private async Task<T> RetryEndpointAsync<T>(
        string endpoint,
        string method,
        object?[] parameters,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (limiter is not null)
                    await limiter.WaitAsync(cancellationToken);

                operations.RecordRpcRequest();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
                var request = new NeoRpcRequest(
                    method,
                    parameters,
                    Interlocked.Increment(ref requestId)
                );
                using var response = await httpClient.PostAsJsonAsync(
                    endpoint,
                    request,
                    timeout.Token
                );
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeout.Token
                );
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    throw new FormatException("RPC response is not an object.");

                if (NeoApplicationLogParser.RequiredString(root, "jsonrpc") != "2.0")
                    throw new FormatException("RPC response must use JSON-RPC 2.0.");

                if (
                    !root.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number
                    || !id.TryGetInt32(out var actualId)
                    || actualId != request.Id
                )
                    throw new FormatException("RPC response id does not match the request.");

                if (
                    root.TryGetProperty("error", out var error)
                    && error.ValueKind != JsonValueKind.Null
                )
                {
                    var code =
                        error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("code", out var number)
                        && number.ValueKind == JsonValueKind.Number
                        && number.TryGetInt32(out var n)
                            ? n
                            : 0;
                    var kind =
                        (method == "getapplicationlog" && code is -100 or -103 or -105 or -32601)
                        || (method == "getblock" && code is -100 or -101 or -32601)
                            ? NeoRpcFailureKind.UnavailableHistory
                            : NeoRpcFailureKind.Transient;

                    throw new NeoRpcException(endpoint, method, kind, $"RPC error {error}");
                }

                if (
                    !root.TryGetProperty("result", out var result)
                    || result.ValueKind == JsonValueKind.Null
                )
                    throw new FormatException("RPC returned no result.");

                var value = parse(result);
                operations.RecordRpcSuccess(endpoint);

                return value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
                when (ex
                        is HttpRequestException
                            or OperationCanceledException
                            or JsonException
                            or FormatException
                            or NeoRpcException
                )
            {
                var failure =
                    ex as NeoRpcException
                    ?? new NeoRpcException(
                        endpoint,
                        method,
                        ex is JsonException or FormatException
                            ? NeoRpcFailureKind.InvalidResponse
                            : NeoRpcFailureKind.Transient,
                        ex is OperationCanceledException ? "Request timed out." : ex.Message,
                        ex
                    );
                operations.RecordRpcFailure();

                if (
                    failure.Kind == NeoRpcFailureKind.NetworkMismatch
                    || attempt >= settings.MaxAttempts
                )
                    throw failure;

                var milliseconds = Math.Min(
                    settings.RetryMaxMilliseconds,
                    settings.RetryBaseMilliseconds * Math.Pow(2, attempt - 1)
                );
                logger.LogWarning(
                    "Retrying Neo RPC {Method} at {Endpoint}, attempt {Attempt}/{MaxAttempts}, failure {Kind}.",
                    method,
                    endpoint,
                    attempt + 1,
                    settings.MaxAttempts,
                    failure.Kind
                );
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
            }
        }
    }

    private sealed record NeoRpcRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object?[] Params,
        [property: JsonPropertyName("id")] int Id
    )
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc => "2.0";
    }
}
