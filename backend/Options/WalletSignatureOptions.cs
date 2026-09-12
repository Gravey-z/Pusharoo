namespace backend.Options;

/// <summary>
/// Configuration that binds wallet signatures to this Pusharoo application.
/// Origins are deliberately separate from CORS: CORS controls browsers, while this
/// allow-list controls what an owner or collaborator is willing to sign for.
/// </summary>
public sealed class WalletSignatureOptions
{
    public const string SectionName = "WalletSignatures";

    public string Audience { get; init; } = "pusharoo-web";

    public string[] AllowedOrigins { get; init; } = [];
}
