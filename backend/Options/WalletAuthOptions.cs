namespace backend.Options;

public sealed class WalletAuthOptions
{
    public const string SectionName = "WalletAuth";

    public string PublicOrigin { get; init; } = string.Empty;
    public bool AllowInsecureLocalhost { get; init; }
    public int ChallengeLifetimeMinutes { get; init; } = 5;
    public int SessionLifetimeHours { get; init; } = 8;
    public string? DataProtectionKeyRingPath { get; init; }
    public string[] TrustedProxyAddresses { get; init; } = [];
}
