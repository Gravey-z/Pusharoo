using backend.Models;

namespace backend.Services;

public sealed class ProjectOwnershipService
{
    public ProjectOwnershipValidationResult ValidateCreatorRecord(ProjectDocument project)
    {
        return string.IsNullOrWhiteSpace(project.CreatedByWalletAddress)
            || string.IsNullOrWhiteSpace(project.CreatedByWalletScriptHash)
            || string.IsNullOrWhiteSpace(project.CreatedByWalletPublicKey)
            ? Fail("Project ownership cannot be verified. This legacy project is read-only until ownership is recovered.")
            : ProjectOwnershipValidationResult.Valid;
    }

    public ProjectOwnershipValidationResult ValidateCanManage(
        ProjectDocument project,
        string? walletAddress)
    {
        var creatorRecord = ValidateCreatorRecord(project);
        if (!creatorRecord.IsValid)
        {
            return creatorRecord;
        }

        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            return Fail("Wallet address is required.");
        }

        var creatorWalletAddress = project.CreatedByWalletAddress;
        if (string.IsNullOrWhiteSpace(creatorWalletAddress))
        {
            return Fail("Project ownership cannot be verified. This legacy project is read-only until ownership is recovered.");
        }

        return string.Equals(
            creatorWalletAddress.Trim(),
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
