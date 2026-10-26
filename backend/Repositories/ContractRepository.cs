using backend.Models;
using backend.Services;
using MongoDB.Driver;

namespace backend.Repositories;

public sealed class ContractRepository(MongoDbContext db) : IContractRepository
{
    public Task InsertAsync(ContractDocument contract, CancellationToken cancellationToken) =>
        db.Contracts.InsertOneAsync(contract, cancellationToken: cancellationToken);

    public Task<ContractDocument?> GetAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    ) =>
        db
            .Contracts.Find(contract =>
                contract.ProjectId == projectId && contract.Id == contractId
            )
            .FirstOrDefaultAsync(cancellationToken)!;

    public Task<ContractDocument?> GetByIdempotencyKeyAsync(
        string projectId,
        string key,
        CancellationToken cancellationToken
    ) =>
        db
            .Contracts.Find(contract =>
                contract.ProjectId == projectId && contract.IdempotencyKey == key
            )
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<ContractDocument>> ListAsync(
        string projectId,
        int skip,
        int take,
        CancellationToken cancellationToken
    ) =>
        await db
            .Contracts.Find(contract => contract.ProjectId == projectId)
            .SortBy(contract => contract.CreatedAt)
            .ThenBy(contract => contract.Id)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken);

    public Task<ContractDocument?> RenameAsync(
        string projectId,
        string contractId,
        string name,
        string normalizedName,
        DateTime updatedAt,
        CancellationToken cancellationToken
    ) =>
        db.Contracts.FindOneAndUpdateAsync(
            contract => contract.ProjectId == projectId && contract.Id == contractId,
            Builders<ContractDocument>
                .Update.Set(contract => contract.Name, name)
                .Set(contract => contract.NormalizedName, normalizedName)
                .Set(contract => contract.UpdatedAt, updatedAt),
            new FindOneAndUpdateOptions<ContractDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken
        )!;
}
