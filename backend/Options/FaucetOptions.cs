namespace backend.Options;

public sealed class FaucetOptions
{
    public const string SectionName = "Faucet";

    public string ContractHash { get; init; } = string.Empty;
    public int NetworkMagic { get; init; } = 894710606;
    public int MaximumQueueDepth { get; init; } = 500;
    public string[] TrustedProxyAddresses { get; init; } = [];
    public string[] TrustedProxyNetworks { get; init; } = [];
}
