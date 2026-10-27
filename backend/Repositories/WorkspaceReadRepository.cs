using backend.Models;
using backend.Services;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class WorkspaceReadRepository(MongoDbContext db)
{
    public async Task<IReadOnlyList<ContractDocument>> GetContractsAsync(
        string projectId,
        CancellationToken cancellationToken
    ) =>
        await db
            .Contracts.Find(item => item.ProjectId == projectId)
            .SortBy(item => item.Name)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ArtifactListItemResponse>> GetArtifactSummariesAsync(
        string projectId,
        CancellationToken cancellationToken
    ) =>
        await db
            .ContractArtifacts.Find(item => item.ProjectId == projectId)
            .SortByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Project(item => new ArtifactListItemResponse(
                item.Id,
                item.ProjectId,
                item.ContractId,
                item.Version,
                item.Notes,
                item.ContractName,
                item.NefFileName,
                item.NefSize,
                item.Summary,
                item.Warnings,
                item.CreatedAt
            ))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DeploymentDocument>> GetProjectDeploymentsAsync(
        string projectId,
        CancellationToken cancellationToken
    ) =>
        await db
            .Deployments.Find(item => item.ProjectId == projectId)
            .SortByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Project<DeploymentDocument>(
                Builders<DeploymentDocument>
                    .Projection.Exclude(item => item.AuthorizationSnapshot)
                    .Exclude(item => item.RecoveryEvidence)
                    .Exclude(item => item.AttemptCapabilityHash)
            )
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ArtifactDocument>> GetContractArtifactsAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    ) =>
        await db
            .ContractArtifacts.Find(item =>
                item.ProjectId == projectId && item.ContractId == contractId
            )
            .SortByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Project<ArtifactDocument>(
                Builders<ArtifactDocument>.Projection.Exclude(item => item.Nef)
            )
            .ToListAsync(cancellationToken);
}
