using System.Globalization;
using backend.Options;
using Microsoft.Extensions.Options;

namespace backend.Services;

public sealed class WalletSignatureRequestValidator(IOptions<WalletSignatureOptions> options)
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
    private readonly WalletSignatureOptions _options = options.Value;
    private readonly HashSet<string> _allowedOrigins = options.Value.AllowedOrigins
        .Select(NormalizeConfiguredOrigin)
        .Where(origin => origin is not null)
        .Select(origin => origin!)
        .ToHashSet(StringComparer.Ordinal);

    public string? Validate(WalletSignatureRequest signature)
    {
        if (string.IsNullOrWhiteSpace(signature.Address)) return "Wallet address is required.";
        if (string.IsNullOrWhiteSpace(signature.ScriptHash)) return "Wallet script hash is required.";
        if (string.IsNullOrWhiteSpace(signature.Network)) return "Wallet network is required.";
        if (string.IsNullOrWhiteSpace(signature.Provider)) return "Wallet provider is required.";
        if (string.IsNullOrWhiteSpace(signature.Origin)) return "Signature origin is required.";
        if (string.IsNullOrWhiteSpace(signature.Audience)) return "Signature audience is required.";
        if (string.IsNullOrWhiteSpace(signature.IssuedAtUtc)) return "Signature timestamp is required.";
        if (string.IsNullOrWhiteSpace(signature.Nonce)) return "Signature nonce is required.";
        if (string.IsNullOrWhiteSpace(signature.Message)) return "Signature message is required.";
        if (string.IsNullOrWhiteSpace(signature.PublicKey) || string.IsNullOrWhiteSpace(signature.Data)) return "Wallet signature response is incomplete.";
        if (!SupportedNetworks.Contains(signature.Network)) return "Unsupported wallet network.";
        if (!SupportedProviders.Contains(signature.Provider)) return "Unsupported wallet provider.";
        if (!IsAllowedOrigin(signature.Origin)) return "Signature origin is not allowed for this Pusharoo application.";
        if (string.IsNullOrWhiteSpace(_options.Audience)
            || !string.Equals(signature.Audience.Trim(), _options.Audience.Trim(), StringComparison.Ordinal))
        {
            return "Signature audience does not match this Pusharoo application.";
        }
        if (!TryParseIssuedAt(signature.IssuedAtUtc, out var issuedAt)) return "Signature timestamp is invalid.";
        if (!IsFresh(issuedAt, DateTimeOffset.UtcNow)) return "Wallet signature has expired. Try again.";
        return signature.Nonce.Trim().Length < 16 ? "Signature nonce is invalid." : null;
    }

    private bool IsAllowedOrigin(string origin)
    {
        var canonicalOrigin = NormalizeSignatureOrigin(origin);
        return canonicalOrigin is not null && _allowedOrigins.Contains(canonicalOrigin);
    }

    private static string? NormalizeConfiguredOrigin(string origin)
    {
        var parsed = ParseOrigin(origin);
        return parsed?.GetLeftPart(UriPartial.Authority);
    }

    private static string? NormalizeSignatureOrigin(string origin)
    {
        var parsed = ParseOrigin(origin);
        if (parsed is null)
        {
            return null;
        }

        var canonicalOrigin = parsed.GetLeftPart(UriPartial.Authority);
        return string.Equals(origin.Trim(), canonicalOrigin, StringComparison.Ordinal)
            ? canonicalOrigin
            : null;
    }

    private static Uri? ParseOrigin(string origin)
    {
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
        {
            return null;
        }

        return uri;
    }

    private static bool TryParseIssuedAt(string issuedAtUtc, out DateTimeOffset issuedAt) => DateTimeOffset.TryParse(
        issuedAtUtc,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
        out issuedAt);

    private static bool IsFresh(DateTimeOffset issuedAt, DateTimeOffset now) => issuedAt >= now.Subtract(MaxSignatureAge)
        && issuedAt <= now.Add(MaxClockSkew);
}
