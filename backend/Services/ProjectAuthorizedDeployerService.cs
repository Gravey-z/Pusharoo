using backend.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed class ProjectAuthorizedDeployerService(MongoDbContext db)
{
    public async Task<ProjectDocument?> AddAsync(
        string projectId,
        ProjectAuthorizedDeployerDocument authorizedDeployer,
        CancellationToken cancellationToken)
    {
        var filter = Builders<ProjectDocument>.Filter.And(
            Builders<ProjectDocument>.Filter.Eq(project => project.Id, projectId),
            Builders<ProjectDocument>.Filter.Not(Builders<ProjectDocument>.Filter.ElemMatch(
                project => project.AuthorizedDeployers,
                item => item.WalletAddress == authorizedDeployer.WalletAddress)),
            BelowAuthorizedDeployerLimit());
        return await db.Projects.FindOneAndUpdateAsync(
            filter,
            Builders<ProjectDocument>.Update.Push(project => project.AuthorizedDeployers, authorizedDeployer),
            new FindOneAndUpdateOptions<ProjectDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public async Task<ProjectDocument?> UpdateAsync(
        string projectId,
        ProjectAuthorizedDeployerDocument authorizedDeployer,
        CancellationToken cancellationToken)
    {
        var filter = Builders<ProjectDocument>.Filter.And(
            Builders<ProjectDocument>.Filter.Eq(project => project.Id, projectId),
            Builders<ProjectDocument>.Filter.ElemMatch(
                project => project.AuthorizedDeployers,
                item => item.WalletAddress == authorizedDeployer.WalletAddress));
        var update = Builders<ProjectDocument>.Update.Combine(
            Builders<ProjectDocument>.Update.Set(
                $"{ProjectDocument.AuthorizedDeployersStorageField}.$.allowedNetworks",
                authorizedDeployer.AllowedNetworks),
            Builders<ProjectDocument>.Update.Set(
                $"{ProjectDocument.AuthorizedDeployersStorageField}.$.updatedAtUtc",
                authorizedDeployer.UpdatedAtUtc));
        return await db.Projects.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<ProjectDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public async Task<ProjectDocument?> RemoveAsync(
        string projectId,
        string walletAddress,
        CancellationToken cancellationToken)
    {
        var filter = Builders<ProjectDocument>.Filter.And(
            Builders<ProjectDocument>.Filter.Eq(project => project.Id, projectId),
            Builders<ProjectDocument>.Filter.ElemMatch(
                project => project.AuthorizedDeployers,
                item => item.WalletAddress == walletAddress));
        return await db.Projects.FindOneAndUpdateAsync(
            filter,
            Builders<ProjectDocument>.Update.PullFilter(
                project => project.AuthorizedDeployers,
                item => item.WalletAddress == walletAddress),
            new FindOneAndUpdateOptions<ProjectDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    private static FilterDefinition<ProjectDocument> BelowAuthorizedDeployerLimit()
    {
        return new BsonDocument("$expr", new BsonDocument("$lt", new BsonArray
        {
            new BsonDocument("$size", new BsonDocument("$ifNull", new BsonArray
            {
                $"${ProjectDocument.AuthorizedDeployersStorageField}", new BsonArray()
            })),
            ProjectAuthorizedDeployerInputValidator.MaxAuthorizedDeployers
        }));
    }
}
