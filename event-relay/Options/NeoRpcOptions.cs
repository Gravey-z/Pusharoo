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
}
