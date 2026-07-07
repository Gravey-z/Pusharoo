using backend.Models;

namespace backend.Services;

public sealed class ProjectOwnershipService
{
    public ProjectOwnershipValidationResult ValidateCanManage(
        ProjectDocument project,
        string? walletAddress)
    {
        if (string.IsNullOrWhiteSpace(project.CreatedByWalletAddress))
        {
            return ProjectOwnershipValidationResult.Valid;
        }

        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            return Fail("Wallet address is required.");
        }

        return string.Equals(
            project.CreatedByWalletAddress.Trim(),
            walletAddress.Trim(),
            StringComparison.Ordinal)
            ? ProjectOwnershipValidationResult.Valid
            : Fail("Only the project creator can manage versions and deployments.");
    }

    private static ProjectOwnershipValidationResult Fail(string error)
    {
        return new ProjectOwnershipValidationResult(false, error);
    }
}

public sealed record ProjectOwnershipValidationResult(bool IsValid, string Error)
{
    public static ProjectOwnershipValidationResult Valid { get; } = new(true, string.Empty);
}
