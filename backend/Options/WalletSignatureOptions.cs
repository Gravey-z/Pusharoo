namespace backend.Options;

public sealed class WalletSignatureOptions
{
    public const string SectionName = "WalletSignatures";

    public string Audience { get; init; } = "pusharoo-web";

    public string[] AllowedOrigins { get; init; } = [];
}
