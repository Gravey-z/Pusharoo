using System.Security.Cryptography;
using System.Text;
using backend.Models;

namespace backend.Services;

public sealed class ProjectCreationSignatureValidator(
    NeoWalletSignatureVerifier signatureVerifier,
    WalletSignatureRequestValidator requestValidator)
{
    public ProjectCreationSignatureValidationResult Validate(CreateProjectRequest request)
    {
        var signature = request.Signature;

        if (signature is null)
        {
            return Fail("Wallet signature is required before creating a project.");
        }

        var requestError = requestValidator.Validate(signature);
        if (requestError is not null)
        {
            return Fail(requestError);
        }

        var expectedMessage = BuildMessage(request, signature);
        if (!string.Equals(signature.Message, expectedMessage, StringComparison.Ordinal))
        {
            return Fail("Wallet signature message does not match the project request.");
        }

        var walletSignatureValidation = signatureVerifier.Verify(signature, expectedMessage);
        if (!walletSignatureValidation.IsValid)
        {
            return Fail(walletSignatureValidation.Error);
        }

        return ProjectCreationSignatureValidationResult.Valid;
    }

    private static string BuildMessage(
        CreateProjectRequest request,
        WalletSignatureRequest signature)
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
