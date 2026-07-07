using backend.Models;

namespace backend.Services;

public static class ResponseMapper
{
    public static ProjectResponse ToResponse(this ProjectDocument project)
    {
        return new ProjectResponse(
            project.Id,
            project.Name,
            project.Description,
            project.CreatedByWalletAddress,
            project.CreatorNetwork,
            project.CreatedAt);
    }

    public static ArtifactResponse ToResponse(this ArtifactDocument artifact)
    {
        return new ArtifactResponse(
            artifact.Id,
            artifact.ProjectId,
            artifact.Version,
            artifact.Notes,
            artifact.ContractName,
            artifact.NefFileName,
            artifact.NefSize,
            artifact.Manifest,
            artifact.Summary,
            artifact.Warnings,
            artifact.CreatedAt);
    }

    public static DeploymentResponse ToResponse(this DeploymentDocument deployment)
    {
        return new DeploymentResponse(
            deployment.Id,
            deployment.ProjectId,
            deployment.ArtifactId,
            deployment.Version,
            deployment.Network,
            deployment.ContractHash,
            deployment.TransactionId,
            deployment.DeployedBy,
            deployment.Notes,
            deployment.CreatedAt);
    }
}
