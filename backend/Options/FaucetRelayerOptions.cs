namespace backend.Options;

public sealed class FaucetRelayerOptions
{
    public const string SectionName = "FaucetRelayer";

    public bool Enabled { get; init; }
    public bool KeyConfigured { get; init; }
    public string PrivateKeyWif { get; init; } = string.Empty;
    public decimal MaximumTransactionFeeGas { get; init; }
    public decimal DailyFeeBudgetGas { get; init; }
    public int PollIntervalSeconds { get; init; } = 3;
}
