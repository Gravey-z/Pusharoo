using backend.Models;

namespace backend.Repositories;

public interface IContractRepository
{
    Task InsertAsync(ContractDocument contract, CancellationToken cancellationToken);

    Task<ContractDocument?> GetAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    );

    Task<ContractDocument?> GetByIdempotencyKeyAsync(
        string projectId,
        string key,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<ContractDocument>> ListAsync(
        string projectId,
        int skip,
        int take,
        CancellationToken cancellationToken
    );

    Task<ContractDocument?> RenameAsync(
        string projectId,
        string contractId,
        string name,
        string normalizedName,
        DateTime updatedAt,
        CancellationToken cancellationToken
    );
}
