namespace Pusharoo.EventRelay.Options;

public sealed class NeoRpcOptions
{
    public const string SectionName = "NeoRpc";

    public string Endpoint { get; init; } = "https://mainnet1.neo.coz.io:443";

    public string? FallbackEndpoint { get; init; }

    public string Network { get; init; } = "neo3:mainnet";

    public uint? StartBlock { get; init; }

    public int PollIntervalSeconds { get; init; } = 15;

    public int MaxBlocksPerPoll { get; init; } = 10;

    public uint ConfirmationBlocks { get; init; } = 1;

    public int CatchUpPollIntervalMilliseconds { get; init; } = 250;

    public int MinRequestIntervalMilliseconds { get; init; } = 100;

    public int RequestTimeoutSeconds { get; init; } = 15;

    public int MaxAttempts { get; init; } = 3;

    public int RetryBaseMilliseconds { get; init; } = 500;

    public int RetryMaxMilliseconds { get; init; } = 5000;

    public int ScannerLeaseSeconds { get; init; } = 60;

    public uint NetworkMagic =>
        Network switch
        {
            "neo3:mainnet" => 860833102,
            "neo3:testnet" => 894710606,
            _ => throw new InvalidOperationException($"Unsupported Neo RPC network: {Network}"),
        };

    public bool StartAtLatest { get; init; }
}
