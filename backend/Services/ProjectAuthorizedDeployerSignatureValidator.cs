using backend.Models;

namespace backend.Services;

public sealed class ProjectAuthorizedDeployerSignatureValidator(
    NeoWalletSignatureVerifier signatureVerifier,
    WalletSignatureRequestValidator requestValidator,
    ProjectOwnershipService ownership)
{
    public ProjectAuthorizedDeployerSignatureValidationResult ValidateAdd(
        ProjectDocument project,
        ProjectAuthorizedDeployerInputValidationResult input,
        WalletSignatureRequest? signature)
        => Validate(project, "authorized-deployers.add", input.WalletAddress, input.AllowedNetworks, signature);

    public ProjectAuthorizedDeployerSignatureValidationResult ValidateUpdate(
        ProjectDocument project,
        ProjectAuthorizedDeployerInputValidationResult input,
        WalletSignatureRequest? signature)
        => Validate(project, "authorized-deployers.update", input.WalletAddress, input.AllowedNetworks, signature);

    public ProjectAuthorizedDeployerSignatureValidationResult ValidateRemove(
        ProjectDocument project,
        ProjectAuthorizedDeployerDocument existing,
        WalletSignatureRequest? signature)
        => Validate(project, "authorized-deployers.remove", existing.WalletAddress, existing.AllowedNetworks, signature);

    private ProjectAuthorizedDeployerSignatureValidationResult Validate(
        ProjectDocument project,
        string action,
        string walletAddress,
        IReadOnlyList<string> allowedNetworks,
        WalletSignatureRequest? signature)
    {
        if (signature is null)
        {
            return Unauthorized("Wallet signature is required.");
        }

        var creatorRecord = ownership.ValidateCreatorRecord(project);
        if (!creatorRecord.IsValid)
        {
            return Forbidden(creatorRecord.Error);
        }

        var requestError = requestValidator.Validate(signature);
        if (requestError is not null)
        {
            return Unauthorized(requestError);
        }

        if (!string.Equals(project.CreatedByWalletAddress, signature.Address.Trim(), StringComparison.Ordinal))
        {
            return Forbidden("Only the project creator can manage authorized deployers.");
        }

        var expectedMessage = BuildMessage(project.Id, action, walletAddress, allowedNetworks, signature);
        if (!string.Equals(signature.Message, expectedMessage, StringComparison.Ordinal))
        {
            return Unauthorized("Wallet signature message does not match the authorized-deployer request.");
        }

        var verification = signatureVerifier.Verify(signature, expectedMessage);
        if (!verification.IsValid)
        {
            return Unauthorized(verification.Error);
        }

        return signatureVerifier.PublicKeysMatch(project.CreatedByWalletPublicKey, signature.PublicKey)
            ? ProjectAuthorizedDeployerSignatureValidationResult.Valid
            : Forbidden("Only the project creator can manage authorized deployers.");
    }

    public static string BuildMessage(
        string projectId,
        string action,
        string walletAddress,
        IReadOnlyList<string> allowedNetworks,
        WalletSignatureRequest signature)
    {
        var networks = allowedNetworks
            .Select(network => network.Trim())
            .OrderBy(network => network, StringComparer.Ordinal);
        return string.Join('\n', new[]
        {
            "Pusharoo authorized deployer authorization",
            "Schema: pusharoo.authorized-deployer.v1",
            $"Action: {action}",
            $"Project ID: {projectId.Trim()}",
            $"Target wallet: {walletAddress.Trim()}",
            $"Allowed networks: {string.Join(',', networks)}",
            $"Owner wallet: {signature.Address.Trim()}",
            $"Owner script hash: {signature.ScriptHash.Trim()}",
            $"Owner network: {signature.Network.Trim()}",
            $"Audience: {signature.Audience.Trim()}",
            $"Origin: {signature.Origin.Trim()}",
            $"Issued at UTC: {signature.IssuedAtUtc.Trim()}",
            $"Nonce: {signature.Nonce.Trim()}"
        });
    }

    private static ProjectAuthorizedDeployerSignatureValidationResult Unauthorized(string error)
        => new(false, StatusCodes.Status401Unauthorized, error);

    private static ProjectAuthorizedDeployerSignatureValidationResult Forbidden(string error)
        => new(false, StatusCodes.Status403Forbidden, error);
}

public sealed record ProjectAuthorizedDeployerSignatureValidationResult(bool IsValid, int StatusCode, string Error)
{
    public static ProjectAuthorizedDeployerSignatureValidationResult Valid { get; } = new(true, StatusCodes.Status204NoContent, string.Empty);
}
