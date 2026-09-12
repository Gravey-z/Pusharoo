using backend.Models;

namespace backend.Services;

public sealed class ProjectCollaboratorSignatureValidator(
    NeoWalletSignatureVerifier signatureVerifier,
    WalletSignatureRequestValidator requestValidator,
    ProjectOwnershipService ownership)
{
    public ProjectCollaboratorSignatureValidationResult ValidateAdd(
        ProjectDocument project,
        ProjectCollaboratorInputValidationResult input,
        long expectedGrantRevision,
        WalletSignatureRequest? signature)
        => Validate(project, "collaborators.add", input, expectedGrantRevision, signature);

    public ProjectCollaboratorSignatureValidationResult ValidateUpdate(
        ProjectDocument project,
        ProjectCollaboratorInputValidationResult input,
        long expectedGrantRevision,
        WalletSignatureRequest? signature)
        => Validate(project, "collaborators.update", input, expectedGrantRevision, signature);

    public ProjectCollaboratorSignatureValidationResult ValidateRemove(
        ProjectDocument project,
        ProjectCollaboratorInputValidationResult input,
        ProjectCollaboratorDocument existing,
        long expectedGrantRevision,
        WalletSignatureRequest? signature)
    {
        var currentInput = input with { Role = existing.Role, AllowedNetworks = [..existing.AllowedNetworks] };
        return Validate(project, "collaborators.remove", currentInput, expectedGrantRevision, signature);
    }

    private ProjectCollaboratorSignatureValidationResult Validate(
        ProjectDocument project,
        string action,
        ProjectCollaboratorInputValidationResult input,
        long expectedGrantRevision,
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
            return Forbidden("Only the project creator can manage collaborators.");
        }

        var expectedMessage = BuildMessage(project.Id, action, input, expectedGrantRevision, signature);
        if (!string.Equals(signature.Message, expectedMessage, StringComparison.Ordinal))
        {
            return Unauthorized("Wallet signature message does not match the collaborator request.");
        }

        var verification = signatureVerifier.Verify(signature, expectedMessage);
        if (!verification.IsValid)
        {
            return Unauthorized(verification.Error);
        }

        return signatureVerifier.PublicKeysMatch(project.CreatedByWalletPublicKey, signature.PublicKey)
            ? ProjectCollaboratorSignatureValidationResult.Valid
            : Forbidden("Only the project creator can manage collaborators.");
    }

    public static string BuildMessage(
        string projectId,
        string action,
        ProjectCollaboratorInputValidationResult input,
        long expectedGrantRevision,
        WalletSignatureRequest signature)
    {
        return string.Join('\n', new[]
        {
            "Pusharoo collaborator authorization",
            "Schema: pusharoo.collaborator.v1",
            $"Action: {action}",
            $"Project ID: {projectId.Trim()}",
            $"Target wallet: {input.WalletAddress}",
            $"Role: {input.Role}",
            $"Allowed networks: {string.Join(',', input.AllowedNetworks)}",
            $"Expected grant revision: {expectedGrantRevision}",
            $"Audience: {signature.Audience.Trim()}",
            $"Origin: {signature.Origin.Trim()}",
            $"Issued at UTC: {signature.IssuedAtUtc.Trim()}",
            $"Nonce: {signature.Nonce.Trim()}"
        });
    }

    private static ProjectCollaboratorSignatureValidationResult Unauthorized(string error)
        => new(false, StatusCodes.Status401Unauthorized, error);

    private static ProjectCollaboratorSignatureValidationResult Forbidden(string error)
        => new(false, StatusCodes.Status403Forbidden, error);
}

public sealed record ProjectCollaboratorSignatureValidationResult(bool IsValid, int StatusCode, string Error)
{
    public static ProjectCollaboratorSignatureValidationResult Valid { get; } = new(true, StatusCodes.Status204NoContent, string.Empty);
}
