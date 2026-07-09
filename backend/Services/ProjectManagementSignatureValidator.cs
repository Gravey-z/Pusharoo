using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using backend.Models;

namespace backend.Services;

public sealed class ProjectManagementSignatureValidator(NeoWalletSignatureVerifier signatureVerifier)
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

    public ProjectManagementSignatureValidationResult ValidateArtifactUpload(
        ProjectDocument project,
        string version,
        string? notes,
        byte[] nef,
        string manifestJson,
        WalletSignatureRequest? signature)
    {
        var commonValidation = ValidateCommon(project, signature);
        if (!commonValidation.IsValid || signature is null)
        {
            return commonValidation;
        }

        var expectedMessage = BuildArtifactUploadMessage(
            project.Id,
            version,
            notes,
            Sha256Hex(nef),
            Sha256Hex(manifestJson),
            signature);

        return ValidateSignedMessage(project, signature, expectedMessage);
    }

    private ProjectManagementSignatureValidationResult ValidateSignedMessage(
        ProjectDocument project,
        WalletSignatureRequest signature,
        string expectedMessage)
    {
        if (!string.Equals(signature.Message, expectedMessage, StringComparison.Ordinal))
        {
            return Fail("Wallet signature message does not match the management request.");
        }

        var signatureValidation = signatureVerifier.Verify(signature, expectedMessage);
        if (!signatureValidation.IsValid)
        {
            return Fail(signatureValidation.Error);
        }

        if (!string.IsNullOrWhiteSpace(project.CreatedByWalletPublicKey))
        {
            return signatureVerifier.PublicKeysMatch(project.CreatedByWalletPublicKey, signature.PublicKey)
                ? ProjectManagementSignatureValidationResult.Valid
                : Fail("Only the project creator can manage versions and deployments.");
        }

        if (!string.IsNullOrWhiteSpace(project.CreatedByWalletAddress))
        {
            return string.Equals(project.CreatedByWalletAddress.Trim(), signature.Address.Trim(), StringComparison.Ordinal)
                ? ProjectManagementSignatureValidationResult.Valid
                : Fail("Only the project creator can manage versions and deployments.");
        }

        return ProjectManagementSignatureValidationResult.Valid;
    }

    private static ProjectManagementSignatureValidationResult ValidateCommon(
        ProjectDocument project,
        WalletSignatureRequest? signature)
    {
        if (signature is null)
        {
            return Fail("Wallet signature is required.");
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
            return Fail("Wallet signature has expired. Try again.");
        }

        if (signature.Nonce.Trim().Length < 16)
        {
            return Fail("Signature nonce is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(project.CreatedByWalletAddress)
            && !string.Equals(project.CreatedByWalletAddress.Trim(), signature.Address.Trim(), StringComparison.Ordinal))
        {
            return Fail("Only the project creator can manage versions and deployments.");
        }

        return ProjectManagementSignatureValidationResult.Valid;
    }

    private static string? ValidateRequiredFields(WalletSignatureRequest signature)
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

    private static string BuildArtifactUploadMessage(
        string projectId,
        string version,
        string? notes,
        string nefHash,
        string manifestHash,
        WalletSignatureRequest signature)
    {
        return string.Join('\n', new[]
        {
            "Pusharoo artifact upload",
            $"Project ID: {projectId.Trim()}",
            $"Version: {version.Trim()}",
            $"Notes SHA-256: {Sha256Hex(NormalizeOptional(notes))}",
            $"NEF SHA-256: {nefHash}",
            $"Manifest SHA-256: {manifestHash}",
            $"Wallet: {signature.Address.Trim()}",
            $"Script hash: {signature.ScriptHash.Trim()}",
            $"Network: {signature.Network.Trim()}",
            $"Origin: {signature.Origin.Trim()}",
            $"Issued at UTC: {signature.IssuedAtUtc.Trim()}",
            $"Nonce: {signature.Nonce.Trim()}"
        });
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

    private static string NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string Sha256Hex(string value)
    {
        return Sha256Hex(Encoding.UTF8.GetBytes(value));
    }

    private static string Sha256Hex(byte[] value)
    {
        var hash = SHA256.HashData(value);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ProjectManagementSignatureValidationResult Fail(string error)
    {
        return new ProjectManagementSignatureValidationResult(false, error);
    }
}

public sealed record ProjectManagementSignatureValidationResult(bool IsValid, string Error)
{
    public static ProjectManagementSignatureValidationResult Valid { get; } = new(true, string.Empty);
}
