using System.Security.Cryptography;
using System.Text;
using backend.Models;

namespace backend.Services;

public sealed class ProjectManagementSignatureValidator(
    NeoWalletSignatureVerifier signatureVerifier,
    WalletSignatureRequestValidator requestValidator)
{
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

    public ProjectManagementSignatureValidationResult ValidateWebhookAdministration(
        ProjectDocument project,
        string operation,
        string requestHash,
        WalletSignatureRequest? signature)
    {
        var commonValidation = ValidateCommon(project, signature);
        if (!commonValidation.IsValid || signature is null)
        {
            return commonValidation;
        }

        if (!WebhookOperations.Contains(operation))
        {
            return Fail("Webhook management operation is invalid.");
        }

        if (requestHash.Length != 64 || !requestHash.All(Uri.IsHexDigit))
        {
            return Fail("Webhook management request hash is invalid.");
        }

        var expectedMessage = BuildWebhookAdministrationMessage(
            project.Id,
            operation,
            requestHash,
            signature);

        return ValidateSignedMessage(project, signature, expectedMessage);
    }

    public ProjectManagementSignatureValidationResult ValidateProjectDeletion(
        ProjectDocument project,
        string projectName,
        WalletSignatureRequest? signature)
    {
        var commonValidation = ValidateCommon(project, signature);
        if (!commonValidation.IsValid || signature is null)
        {
            return commonValidation;
        }

        if (!string.Equals(project.Name.Trim(), projectName.Trim(), StringComparison.Ordinal))
        {
            return Fail("Type the exact project name to confirm deletion.");
        }

        var expectedMessage = BuildProjectDeletionMessage(project.Id, project.Name, signature);
        return ValidateSignedMessage(project, signature, expectedMessage);
    }

    private static readonly HashSet<string> WebhookOperations = new(StringComparer.Ordinal)
    {
        "subscriptions.read",
        "subscriptions.create",
        "subscriptions.update",
        "subscriptions.delete",
        "deliveries.read",
        "deliveries.test",
        "deliveries.redeliver",
        "payments.create",
        "payments.confirm",
        "payments.read"
    };

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

    private ProjectManagementSignatureValidationResult ValidateCommon(
        ProjectDocument project,
        WalletSignatureRequest? signature)
    {
        if (signature is null)
        {
            return Fail("Wallet signature is required.");
        }

        var requestError = requestValidator.Validate(signature);
        if (requestError is not null)
        {
            return Fail(requestError);
        }

        if (!string.IsNullOrWhiteSpace(project.CreatedByWalletAddress)
            && !string.Equals(project.CreatedByWalletAddress.Trim(), signature.Address.Trim(), StringComparison.Ordinal))
        {
            return Fail("Only the project creator can manage versions and deployments.");
        }

        return ProjectManagementSignatureValidationResult.Valid;
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

    private static string BuildWebhookAdministrationMessage(
        string projectId,
        string operation,
        string requestHash,
        WalletSignatureRequest signature)
    {
        return string.Join('\n', new[]
        {
            "Pusharoo webhook administration",
            $"Project ID: {projectId.Trim()}",
            $"Operation: {operation}",
            $"Request SHA-256: {requestHash.Trim().ToLowerInvariant()}",
            $"Wallet: {signature.Address.Trim()}",
            $"Script hash: {signature.ScriptHash.Trim()}",
            $"Network: {signature.Network.Trim()}",
            $"Origin: {signature.Origin.Trim()}",
            $"Issued at UTC: {signature.IssuedAtUtc.Trim()}",
            $"Nonce: {signature.Nonce.Trim()}"
        });
    }

    private static string BuildProjectDeletionMessage(
        string projectId,
        string projectName,
        WalletSignatureRequest signature)
    {
        return string.Join('\n', new[]
        {
            "Pusharoo project deletion",
            $"Project ID: {projectId.Trim()}",
            $"Project: {projectName.Trim()}",
            "Confirmation: DELETE",
            $"Wallet: {signature.Address.Trim()}",
            $"Script hash: {signature.ScriptHash.Trim()}",
            $"Network: {signature.Network.Trim()}",
            $"Origin: {signature.Origin.Trim()}",
            $"Issued at UTC: {signature.IssuedAtUtc.Trim()}",
            $"Nonce: {signature.Nonce.Trim()}"
        });
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
