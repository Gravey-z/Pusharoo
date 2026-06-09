using backend.Models;
using backend.Repositories;
using MongoDB.Bson;

namespace backend.Services;

public sealed class DeploymentService(IDeploymentRepository deployments)
{
    public async Task<DeploymentDocument> CreateAsync(
        string projectId,
        ArtifactDocument artifact,
        CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var deployment = new DeploymentDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ProjectId = projectId,
            ArtifactId = artifact.Id,
            Version = artifact.Version,
            Network = request.Network.Trim(),
            ContractHash = TrimToNull(request.ContractHash),
            TransactionId = TrimToNull(request.TransactionId),
            DeployedBy = request.DeployedBy.Trim(),
            Notes = TrimToNull(request.Notes),
            CreatedAt = DateTime.UtcNow
        };

        await deployments.InsertAsync(deployment, cancellationToken);

        return deployment;
    }

    public async Task<IReadOnlyList<DeploymentDocument>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        return await deployments.GetByProjectIdAsync(projectId, cancellationToken);
    }

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
