using backend.Models;
using backend.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace backend.Services;

public sealed partial class ContractWriteGuard(
    MongoDbContext db,
    IOptions<MultiContractOptions> options
)
{
    public async Task<string> AcquireAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!options.Value.WritesEnabled)
            throw new ContractOperationException(
                503,
                "contracts_not_ready",
                "Contract management is not enabled."
            );

        var token = Guid.NewGuid().ToString("N");
        var result = await db.Projects.UpdateOneAsync(
            project =>
                project.Id == projectId
                && project.ContractSchemaVersion == 1
                && !project.IsDeleting
                && project.ContractWriteToken == null,
            Builders<ProjectDocument>.Update.Set(project => project.ContractWriteToken, token),
            cancellationToken: cancellationToken
        );

        if (result.MatchedCount == 0)
        {
            var current = await db
                .Projects.Find(project => project.Id == projectId)
                .FirstOrDefaultAsync(cancellationToken);
            if (
                current is
                { IsDeleting: false, ContractSchemaVersion: 1, ContractWriteToken: not null }
            )
                throw new ContractOperationException(
                    409,
                    "project_busy",
                    "The project is busy. Retry shortly."
                );

            throw new ContractOperationException(
                409,
                "project_not_ready",
                "The project requires migration or is being deleted. Retry when it is ready."
            );
        }

        try
        {
            await EnsureReadinessAsync(projectId, cancellationToken);

            return token;
        }
        catch
        {
            await ReleaseAsync(projectId, token);
            throw;
        }
    }

    public Task ReleaseAsync(string projectId, string token) =>
        db.Projects.UpdateOneAsync(
            project => project.Id == projectId && project.ContractWriteToken == token,
            Builders<ProjectDocument>.Update.Unset(project => project.ContractWriteToken),
            cancellationToken: CancellationToken.None
        );

    public async Task BeginDeletionAsync(string projectId, CancellationToken cancellationToken)
    {
        var result = await db.Projects.UpdateOneAsync(
            project => project.Id == projectId && project.ContractWriteToken == null,
            Builders<ProjectDocument>.Update.Set(project => project.IsDeleting, true),
            cancellationToken: cancellationToken
        );

        if (result.MatchedCount == 0)
            throw new ContractOperationException(
                409,
                "project_busy",
                "The project is busy. Retry deletion after contract management completes."
            );

        if (
            await db
                .Contracts.Find(contract =>
                    contract.ProjectId == projectId
                    && (
                        contract.TestnetDeploymentWriteToken != null
                        || contract.MainnetDeploymentWriteToken != null
                    )
                )
                .AnyAsync(cancellationToken)
        )
            throw new ContractOperationException(
                409,
                "project_busy",
                "A deployment action is running. Retry deletion after it completes."
            );
    }
}
