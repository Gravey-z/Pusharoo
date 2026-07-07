using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using backend.Models;

namespace backend.Services;

public static class ProjectCreationSignatureValidator
{
    private static readonly HashSet<string> SupportedNetworks = new(StringComparer.Ordinal)
    {
        "neo3:private",
        "neo3:testnet",
        "neo3:mainnet"
    };

    private static readonly HashSet<string> SupportedProviders = new(StringComparer.Ordinal)
    {
        "neoline",
        "onegate",
        "walletconnect"
    };

    public static bool TryValidate(
        CreateProjectRequest request,
        out string error)
    {
        error = string.Empty;
        var signature = request.Signature;

        if (signature is null)
        {
            error = "Wallet signature is required before creating a project.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature.Address))
        {
            error = "Wallet address is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature.ScriptHash))
        {
            error = "Wallet script hash is required.";
            return false;
        }

        if (!SupportedNetworks.Contains(signature.Network))
        {
            error = "Unsupported wallet network.";
            return false;
        }

        if (!SupportedProviders.Contains(signature.Provider))
        {
            error = "Unsupported wallet provider.";
            return false;
        }

        if (!Uri.TryCreate(signature.Origin, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps))
        {
            error = "Signature origin is invalid.";
            return false;
        }

        if (!DateTimeOffset.TryParse(
                signature.IssuedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var issuedAt))
        {
            error = "Signature timestamp is invalid.";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (issuedAt < now.AddMinutes(-10) || issuedAt > now.AddMinutes(2))
        {
            error = "Wallet signature has expired. Try creating the project again.";
            return false;
        }

        if (signature.Nonce.Trim().Length < 16)
        {
            error = "Signature nonce is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature.PublicKey) || string.IsNullOrWhiteSpace(signature.Data))
        {
            error = "Wallet signature response is incomplete.";
            return false;
        }

        var expectedMessage = BuildMessage(
            request.Name,
            request.Description,
            signature.Address,
            signature.ScriptHash,
            signature.Network,
            signature.Origin,
            signature.IssuedAtUtc,
            signature.Nonce);

        if (!string.Equals(signature.Message, expectedMessage, StringComparison.Ordinal))
        {
            error = "Wallet signature message does not match the project request.";
            return false;
        }

        return true;
    }

    private static string BuildMessage(
        string name,
        string? description,
        string address,
        string scriptHash,
        string network,
        string origin,
        string issuedAtUtc,
        string nonce)
    {
        var normalizedName = name.Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : description.Trim();

        return string.Join('\n', new[]
        {
            "Pusharoo project creation",
            $"Project: {normalizedName}",
            $"Description SHA-256: {Sha256Hex(normalizedDescription)}",
            $"Wallet: {address.Trim()}",
            $"Script hash: {scriptHash.Trim()}",
            $"Network: {network.Trim()}",
            $"Origin: {origin.Trim()}",
            $"Issued at UTC: {issuedAtUtc.Trim()}",
            $"Nonce: {nonce.Trim()}"
        });
    }

    private static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
