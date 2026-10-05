namespace backend.Options;

public sealed class RelayGatewayOptions
{
    public const string SectionName = "RelayGateway";
    public RelayTargetOptions Testnet { get; init; } = new();
    public RelayTargetOptions Mainnet { get; init; } = new();
}

public sealed class RelayTargetOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string ServiceToken { get; init; } = string.Empty;
}
