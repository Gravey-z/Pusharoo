using backend.Models;

namespace backend.Services;

public sealed class ProjectAuthorizationService(ProjectOwnershipService ownership)
{
    public ProjectAuthorizationResult CanAdministerProject(ProjectDocument project, string? walletAddress)
        => CanOwnerOnly(project, walletAddress, "Only the project creator can administer this project.");

    public ProjectAuthorizationResult CanUploadArtifacts(ProjectDocument project, string? walletAddress)
        => CanOwnerOnly(project, walletAddress, "Only the project creator can upload artifacts.");

    public ProjectAuthorizationResult CanManageWebhooks(ProjectDocument project, string? walletAddress)
        => CanOwnerOnly(project, walletAddress, "Only the project creator can manage webhooks and Relay payments.");

    public ProjectAuthorizationResult CanDeployToNetwork(ProjectDocument project, string? walletAddress, string? network)
    {
        var creator = ownership.ValidateCreatorRecord(project);
        if (!creator.IsValid)
        {
            return Denied(StatusCodes.Status403Forbidden, creator.Error);
        }
        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            return Denied(StatusCodes.Status401Unauthorized, "Wallet authentication is required.");
        }
        if (string.IsNullOrWhiteSpace(network))
        {
            return Denied(StatusCodes.Status400BadRequest, "Deployment network is required.");
        }

        var canonicalWallet = walletAddress.Trim();
        if (string.Equals(project.CreatedByWalletAddress, canonicalWallet, StringComparison.Ordinal))
        {
            return new ProjectAuthorizationResult(true, StatusCodes.Status204NoContent, string.Empty, true, null);
        }

        var grant = project.Collaborators.FirstOrDefault(item =>
            string.Equals(item.WalletAddress, canonicalWallet, StringComparison.Ordinal)
            && string.Equals(item.Role, "deployer", StringComparison.Ordinal)
            && item.AllowedNetworks.Contains(network.Trim(), StringComparer.Ordinal));
        return grant is null
            ? Denied(StatusCodes.Status403Forbidden, "This wallet is not authorized to deploy this project on the selected network.")
            : new ProjectAuthorizationResult(true, StatusCodes.Status204NoContent, string.Empty, false, grant.GrantRevision);
    }

    private ProjectAuthorizationResult CanOwnerOnly(ProjectDocument project, string? walletAddress, string deniedMessage)
    {
        var creator = ownership.ValidateCreatorRecord(project);
        if (!creator.IsValid)
        {
            return Denied(StatusCodes.Status403Forbidden, creator.Error);
        }
        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            return Denied(StatusCodes.Status401Unauthorized, "Wallet authentication is required.");
        }

        return string.Equals(project.CreatedByWalletAddress, walletAddress.Trim(), StringComparison.Ordinal)
            ? new ProjectAuthorizationResult(true, StatusCodes.Status204NoContent, string.Empty, true, null)
            : Denied(StatusCodes.Status403Forbidden, deniedMessage);
    }

    private static ProjectAuthorizationResult Denied(int statusCode, string error)
        => new(false, statusCode, error, false, null);
}

public sealed record ProjectAuthorizationResult(
    bool IsAllowed,
    int StatusCode,
    string Error,
    bool IsOwner,
    long? GrantRevision);
