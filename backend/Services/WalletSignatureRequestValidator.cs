using System.Globalization;

namespace backend.Services;

public sealed class WalletSignatureRequestValidator
{
    private static readonly TimeSpan MaxSignatureAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> SupportedNetworks = new(StringComparer.Ordinal)
    {
        "neo3:testnet",
        "neo3:mainnet"
    };
    private static readonly HashSet<string> SupportedProviders = new(StringComparer.Ordinal)
    {
        "neoline",
        "onegate",
        "walletconnect"
    };

    public string? Validate(WalletSignatureRequest signature)
    {
        if (string.IsNullOrWhiteSpace(signature.Address)) return "Wallet address is required.";
        if (string.IsNullOrWhiteSpace(signature.ScriptHash)) return "Wallet script hash is required.";
        if (string.IsNullOrWhiteSpace(signature.Network)) return "Wallet network is required.";
        if (string.IsNullOrWhiteSpace(signature.Provider)) return "Wallet provider is required.";
        if (string.IsNullOrWhiteSpace(signature.Origin)) return "Signature origin is required.";
        if (string.IsNullOrWhiteSpace(signature.IssuedAtUtc)) return "Signature timestamp is required.";
        if (string.IsNullOrWhiteSpace(signature.Nonce)) return "Signature nonce is required.";
        if (string.IsNullOrWhiteSpace(signature.Message)) return "Signature message is required.";
        if (string.IsNullOrWhiteSpace(signature.PublicKey) || string.IsNullOrWhiteSpace(signature.Data)) return "Wallet signature response is incomplete.";
        if (!SupportedNetworks.Contains(signature.Network)) return "Unsupported wallet network.";
        if (!SupportedProviders.Contains(signature.Provider)) return "Unsupported wallet provider.";
        if (!HasValidOrigin(signature.Origin)) return "Signature origin is invalid.";
        if (!TryParseIssuedAt(signature.IssuedAtUtc, out var issuedAt)) return "Signature timestamp is invalid.";
        if (!IsFresh(issuedAt, DateTimeOffset.UtcNow)) return "Wallet signature has expired. Try again.";
        return signature.Nonce.Trim().Length < 16 ? "Signature nonce is invalid." : null;
    }

    private static bool HasValidOrigin(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool TryParseIssuedAt(string issuedAtUtc, out DateTimeOffset issuedAt) => DateTimeOffset.TryParse(
        issuedAtUtc,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
        out issuedAt);

    private static bool IsFresh(DateTimeOffset issuedAt, DateTimeOffset now) => issuedAt >= now.Subtract(MaxSignatureAge)
        && issuedAt <= now.Add(MaxClockSkew);
}
