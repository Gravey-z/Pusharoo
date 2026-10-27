using backend.Models;

namespace backend.Services;

public sealed class DeploymentWorkflowService(
    ProjectService projects,
    ArtifactService artifacts,
    DeploymentService deployments,
    ProjectOwnershipService ownership,
    ProjectContractLoader contractLoader
)
{
    public async Task<DeploymentWorkflowResult<ProjectDocument>> LoadProjectAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken);

        return project is null
            ? DeploymentWorkflowResult<ProjectDocument>.NotFound("Project was not found.")
            : DeploymentWorkflowResult<ProjectDocument>.Success(project);
    }

    public async Task<DeploymentWorkflowResult<ProjectArtifactContext>> LoadOwnedArtifactAsync(
        string projectId,
        string artifactId,
        string deployedBy,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        var projectResult = await LoadProjectAsync(projectId, cancellationToken);

        if (!projectResult.IsSuccess || projectResult.Value is null)
        {
            return DeploymentWorkflowResult<ProjectArtifactContext>.Failure(projectResult);
        }

        var ownershipResult = ownership.ValidateCanManage(projectResult.Value, deployedBy);

        if (!ownershipResult.IsValid)
        {
            return DeploymentWorkflowResult<ProjectArtifactContext>.Forbidden(
                ownershipResult.Error
            );
        }

        var artifact = await artifacts.GetByIdAsync(artifactId, cancellationToken);

        if (
            artifact is null
            || artifact.ProjectId != projectId
            || artifact.ContractId != contractId
        )
        {
            return DeploymentWorkflowResult<ProjectArtifactContext>.BadRequest(
                "Artifact does not belong to this project."
            );
        }

        if (contractId is not null)
            await contractLoader.LoadAsync(projectId, contractId, cancellationToken);

        return DeploymentWorkflowResult<ProjectArtifactContext>.Success(
            new ProjectArtifactContext(projectResult.Value, artifact)
        );
    }

    public async Task<
        DeploymentWorkflowResult<ProjectDeploymentAttemptContext>
    > LoadOwnedAttemptAsync(
        string projectId,
        string deploymentId,
        string deployedBy,
        string errorMessage,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        var projectTask = projects.GetByIdAsync(projectId, cancellationToken);
        var attemptTask = deployments.GetByIdAsync(deploymentId, cancellationToken);
        await Task.WhenAll(projectTask, attemptTask);
        var project = await projectTask;
        var attempt = await attemptTask;

        if (
            project is null
            || attempt is null
            || attempt.ProjectId != projectId
            || attempt.ContractId != contractId
        )
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.NotFound(
                "Deployment attempt was not found."
            );
        }

        var ownershipResult = ownership.ValidateCanManage(project, deployedBy);

        if (
            !ownershipResult.IsValid
            || !string.Equals(attempt.DeployedBy, deployedBy.Trim(), StringComparison.Ordinal)
        )
        {
            return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Forbidden(
                errorMessage
            );
        }

        if (contractId is not null)
            await contractLoader.LoadAsync(projectId, contractId, cancellationToken);

        return DeploymentWorkflowResult<ProjectDeploymentAttemptContext>.Success(
            new ProjectDeploymentAttemptContext(project, attempt)
        );
    }

    public async Task<string> DetermineOperationAsync(
        string projectId,
        string network,
        CancellationToken cancellationToken,
        string? contractId = null
    )
    {
        if (contractId is not null)
            await contractLoader.LoadAsync(projectId, contractId, cancellationToken);
        var current = await deployments.GetForScopeAsync(projectId, contractId, cancellationToken);

        return current.Any(deployment =>
            string.Equals(deployment.Network, network.Trim(), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(deployment.ContractHash)
            && (string.IsNullOrWhiteSpace(deployment.Status) || deployment.Status == "confirmed")
        )
            ? "update"
            : "deploy";
    }
}

public sealed record ProjectArtifactContext(ProjectDocument Project, ArtifactDocument Artifact);

public sealed record ProjectDeploymentAttemptContext(
    ProjectDocument Project,
    DeploymentDocument Attempt
);
