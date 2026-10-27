using backend.Models;

namespace backend.Services;

public sealed class ContractArtifactService(
    ProjectContractLoader loader,
    ProjectAuthorizationService authorization,
    ContractWriteGuard writes,
    ArtifactUploadService uploads
)
{
    public async Task<ProjectContractContext> LoadForUploadAsync(
        string projectId,
        string contractId,
        string? walletAddress,
        CancellationToken cancellationToken
    )
    {
        var context = await loader.LoadAsync(projectId, contractId, cancellationToken);
        var access = authorization.CanUploadArtifacts(context.Project, walletAddress);
        if (!access.IsAllowed)
            throw new ContractOperationException(
                access.StatusCode,
                "artifact_upload_denied",
                access.Error
            );

        return context;
    }

    public async Task<ArtifactUploadResult> UploadAsync(
        ArtifactUploadInput input,
        string? walletAddress,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(input.ContractId))
            throw new ContractOperationException(
                400,
                "contract_required",
                "Choose a contract to upload an artifact."
            );

        var token = await writes.AcquireAsync(input.ProjectId, cancellationToken);
        try
        {
            await LoadForUploadAsync(
                input.ProjectId,
                input.ContractId,
                walletAddress,
                cancellationToken
            );
            return await uploads.SaveAsync(input, cancellationToken);
        }
        finally
        {
            await writes.ReleaseAsync(input.ProjectId, token);
        }
    }
}
