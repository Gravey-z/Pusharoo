namespace backend.Models;

public sealed record ContractWorkspaceSummary(
    ContractResponse Contract,
    int ArtifactCount,
    int DeploymentCount,
    ArtifactListItemResponse? LatestArtifact,
    IReadOnlyList<DeploymentResponse> CurrentDeployments
);

public sealed record ProjectWorkspaceResponse(
    ProjectResponse Project,
    IReadOnlyList<ContractWorkspaceSummary> Contracts,
    int ArtifactCount,
    int DeploymentCount,
    IReadOnlyList<DeploymentResponse> RecentDeployments
);

public sealed record ContractOverviewResponse(
    ProjectResponse Project,
    ContractResponse Contract,
    IReadOnlyList<ArtifactResponse> Artifacts,
    ArtifactResponse? LatestArtifact,
    IReadOnlyList<DeploymentResponse> Deployments,
    DeploymentResponse? LatestDeployment,
    bool Deployed
);
