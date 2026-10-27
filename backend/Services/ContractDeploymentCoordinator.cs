using backend.Models;
using MongoDB.Driver;

namespace backend.Services;

public sealed class ContractDeploymentCoordinator(
    MongoDbContext db,
    ContractWriteGuard projectWrites,
    ProjectContractLoader loader
)
{
    public async Task EnsureReadyAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    )
    {
        await loader.LoadAsync(projectId, contractId, cancellationToken);
        var token = await AcquireProjectAsync(projectId, cancellationToken);
        await projectWrites.ReleaseAsync(projectId, token);
    }

    public async Task<T> ExecuteAsync<T>(
        string projectId,
        string contractId,
        string network,
        Func<Task<T>> action,
        CancellationToken cancellationToken
    )
    {
        if (network is not ("neo3:testnet" or "neo3:mainnet"))
            throw new ContractOperationException(
                400,
                "invalid_network",
                "Choose Neo N3 TestNet or MainNet."
            );

        await loader.LoadAsync(projectId, contractId, cancellationToken);
        var projectToken = await AcquireProjectAsync(projectId, cancellationToken);
        var token = Guid.NewGuid().ToString("N");
        var tokenField =
            network == "neo3:testnet"
                ? "testnetDeploymentWriteToken"
                : "mainnetDeploymentWriteToken";
        var filter =
            Builders<ContractDocument>.Filter.Where(contract =>
                contract.Id == contractId && contract.ProjectId == projectId
            ) & Builders<ContractDocument>.Filter.Eq<string?>(tokenField, null);
        try
        {
            var result = await db.Contracts.UpdateOneAsync(
                filter,
                Builders<ContractDocument>.Update.Set(tokenField, token),
                cancellationToken: cancellationToken
            );
            if (result.MatchedCount == 0)
                throw new ContractOperationException(
                    409,
                    "deployment_busy",
                    "Another deployment action is running for this contract and network. Retry shortly."
                );
        }
        finally
        {
            await projectWrites.ReleaseAsync(projectId, projectToken);
        }

        try
        {
            return await action();
        }
        finally
        {
            await db.Contracts.UpdateOneAsync(
                Builders<ContractDocument>.Filter.Where(contract =>
                    contract.Id == contractId && contract.ProjectId == projectId
                ) & Builders<ContractDocument>.Filter.Eq(tokenField, token),
                Builders<ContractDocument>.Update.Unset(tokenField),
                cancellationToken: CancellationToken.None
            );
        }
    }

    private async Task<string> AcquireProjectAsync(
        string projectId,
        CancellationToken cancellationToken
    )
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await projectWrites.AcquireAsync(projectId, cancellationToken);
            }
            catch (ContractOperationException error)
                when (error.Code == "project_busy" && retry < 40)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }
}
