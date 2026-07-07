using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using backend.Models;

namespace backend.Services;

public sealed class ProjectCreationSignatureValidator
{
    private static readonly TimeSpan MaxSignatureAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);
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

    public ProjectCreationSignatureValidationResult Validate(CreateProjectRequest request)
    {
        var signature = request.Signature;

        if (signature is null)
        {
            return Fail("Wallet signature is required before creating a project.");
        }

        var requiredFieldError = ValidateRequiredFields(signature);
        if (requiredFieldError is not null)
        {
            return Fail(requiredFieldError);
        }

        if (!SupportedNetworks.Contains(signature.Network))
        {
            return Fail("Unsupported wallet network.");
        }

        if (!SupportedProviders.Contains(signature.Provider))
        {
            return Fail("Unsupported wallet provider.");
        }

        if (!HasValidOrigin(signature.Origin))
        {
            return Fail("Signature origin is invalid.");
        }

        if (!TryParseIssuedAt(signature.IssuedAtUtc, out var issuedAt))
        {
            return Fail("Signature timestamp is invalid.");
        }

        if (!IsFresh(issuedAt, DateTimeOffset.UtcNow))
        {
            return Fail("Wallet signature has expired. Try creating the project again.");
        }

        if (signature.Nonce.Trim().Length < 16)
        {
            return Fail("Signature nonce is invalid.");
        }

        var expectedMessage = BuildMessage(request, signature);
        if (!string.Equals(signature.Message, expectedMessage, StringComparison.Ordinal))
        {
            return Fail("Wallet signature message does not match the project request.");
        }

        return ProjectCreationSignatureValidationResult.Valid;
    }

    private static string? ValidateRequiredFields(ProjectCreationSignatureRequest signature)
    {
        if (string.IsNullOrWhiteSpace(signature.Address))
        {
            return "Wallet address is required.";
        }

        if (string.IsNullOrWhiteSpace(signature.ScriptHash))
        {
            return "Wallet script hash is required.";
        }

        if (string.IsNullOrWhiteSpace(signature.Network))
        {
            return "Wallet network is required.";
        }

        if (string.IsNullOrWhiteSpace(signature.Provider))
        {
            return "Wallet provider is required.";
        }

        if (string.IsNullOrWhiteSpace(signature.Origin))
        {
            return "Signature origin is required.";
        }

        if (string.IsNullOrWhiteSpace(signature.IssuedAtUtc))
        {
            return "Signature timestamp is required.";
        }

        if (string.IsNullOrWhiteSpace(signature.Nonce))
        {
            return "Signature nonce is required.";
        }

        if (string.IsNullOrWhiteSpace(signature.Message))
        {
            return "Signature message is required.";
        }

        if (string.IsNullOrWhiteSpace(signature.PublicKey) || string.IsNullOrWhiteSpace(signature.Data))
        {
            return "Wallet signature response is incomplete.";
        }

        return null;
    }

    private static bool HasValidOrigin(string origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool TryParseIssuedAt(string issuedAtUtc, out DateTimeOffset issuedAt)
    {
        return DateTimeOffset.TryParse(
            issuedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out issuedAt);
    }

    private static bool IsFresh(DateTimeOffset issuedAt, DateTimeOffset now)
    {
        return issuedAt >= now.Subtract(MaxSignatureAge)
            && issuedAt <= now.Add(MaxClockSkew);
    }

    private static string BuildMessage(
        CreateProjectRequest request,
        ProjectCreationSignatureRequest signature)
    {
        var normalizedName = request.Name.Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(request.Description)
            ? string.Empty
            : request.Description.Trim();

        return string.Join('\n', new[]
        {
            "Pusharoo project creation",
            $"Project: {normalizedName}",
            $"Description SHA-256: {Sha256Hex(normalizedDescription)}",
            $"Wallet: {signature.Address.Trim()}",
            $"Script hash: {signature.ScriptHash.Trim()}",
            $"Network: {signature.Network.Trim()}",
            $"Origin: {signature.Origin.Trim()}",
            $"Issued at UTC: {signature.IssuedAtUtc.Trim()}",
            $"Nonce: {signature.Nonce.Trim()}"
        });
    }

    private static ProjectCreationSignatureValidationResult Fail(string error)
    {
        return new ProjectCreationSignatureValidationResult(false, error);
    }

    private static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record ProjectCreationSignatureValidationResult(bool IsValid, string Error)
{
    public static ProjectCreationSignatureValidationResult Valid { get; } = new(true, string.Empty);
}
