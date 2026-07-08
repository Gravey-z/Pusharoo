namespace backend.Options;

public sealed class NeoRpcOptions
{
    public const string SectionName = "NeoRpc";

    public Dictionary<string, NeoNetworkRpcOptions> Networks { get; init; } = [];
}

public sealed class NeoNetworkRpcOptions
{
    public string Endpoint { get; init; } = string.Empty;

    public string ContractManagementHash { get; init; } = "0xfffdc93764dbaddd97c48f252a53ea4643faa3fd";
}
