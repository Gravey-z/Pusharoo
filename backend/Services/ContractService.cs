using backend.Models;
using backend.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;

namespace backend.Services;

public sealed class ContractService(
    IContractRepository contracts,
    ProjectContractLoader loader,
    ProjectAuthorizationService authorization,
    ContractWriteGuard writes,
    TimeProvider timeProvider
)
{
    public async Task<ContractDocument> CreateAsync(
        string projectId,
        CreateContractRequest request,
        string? walletAddress,
        string? idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        var project = await loader.LoadProjectAsync(projectId, cancellationToken);
        RequireOwner(project, walletAddress);
        var name = ValidateName(request.Name);
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();

        if (request.Description?.Length > 2_000)
            throw new ContractOperationException(
                400,
                "invalid_description",
                "Contract description must be at most 2000 characters."
            );

        if (
            idempotencyKey is not null
            && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        )
            throw new ContractOperationException(
                400,
                "invalid_idempotency_key",
                "Idempotency-Key must be between 1 and 128 characters."
            );

        var scopedKey = idempotencyKey is null
            ? null
            : WorkspaceIdempotency.Scope(
                walletAddress!.Trim(),
                project.Id,
                "contract.create",
                idempotencyKey.Trim()
            );
        var payloadHash = WorkspaceIdempotency.Hash(name, description);
        var token = await writes.AcquireAsync(project.Id, cancellationToken);

        try
        {
            if (scopedKey is not null)
            {
                var existing = await contracts.GetByIdempotencyKeyAsync(
                    project.Id,
                    scopedKey,
                    cancellationToken
                );

                if (existing is not null)
                {
                    if (existing.IdempotencyPayloadHash != payloadHash)
                        throw new ContractOperationException(
                            409,
                            "idempotency_conflict",
                            "Idempotency-Key was already used with a different contract request."
                        );

                    return existing;
                }
            }

            var now = GetStorageTime();
            var contract = new ContractDocument
            {
                Id = ObjectId.GenerateNewId().ToString(),
                ProjectId = project.Id,
                Name = name,
                NormalizedName = name.ToUpperInvariant(),
                Description = description,
                CreatedAt = now,
                UpdatedAt = now,
                IdempotencyKey = scopedKey,
                IdempotencyPayloadHash = payloadHash,
            };

            await contracts.InsertAsync(contract, cancellationToken);
            return contract;
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw DuplicateName();
        }
        finally
        {
            await writes.ReleaseAsync(project.Id, token);
        }
    }

    public async Task<IReadOnlyList<ContractDocument>> ListAsync(
        string projectId,
        int skip,
        int take,
        CancellationToken cancellationToken
    )
    {
        await loader.LoadProjectAsync(projectId, cancellationToken);

        if (skip < 0 || skip > 1_000_000 || take < 1 || take > 100)
            throw new ContractOperationException(
                400,
                "invalid_pagination",
                "Skip must be between 0 and 1000000; take must be between 1 and 100."
            );

        return await contracts.ListAsync(projectId, skip, take, cancellationToken);
    }

    public async Task<ContractDocument> GetAsync(
        string projectId,
        string contractId,
        CancellationToken cancellationToken
    ) => (await loader.LoadAsync(projectId, contractId, cancellationToken)).Contract;

    public async Task<ContractDocument> RenameAsync(
        string projectId,
        string contractId,
        RenameContractRequest request,
        string? walletAddress,
        CancellationToken cancellationToken
    )
    {
        var context = await loader.LoadAsync(projectId, contractId, cancellationToken);
        RequireOwner(context.Project, walletAddress);
        var name = ValidateName(request.Name);
        var token = await writes.AcquireAsync(context.Project.Id, cancellationToken);

        try
        {
            return await contracts.RenameAsync(
                    projectId,
                    context.Contract.Id,
                    name,
                    name.ToUpperInvariant(),
                    GetStorageTime(),
                    cancellationToken
                )
                ?? throw new ContractOperationException(
                    404,
                    "contract_not_found",
                    "Contract was not found."
                );
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw DuplicateName();
        }
        catch (MongoCommandException error) when (error.Code == 11000)
        {
            throw DuplicateName();
        }
        finally
        {
            await writes.ReleaseAsync(context.Project.Id, token);
        }
    }

    private void RequireOwner(ProjectDocument project, string? walletAddress)
    {
        var access = authorization.CanAdministerProject(project, walletAddress);

        if (!access.IsAllowed)
            throw new ContractOperationException(
                access.StatusCode,
                "contract_management_denied",
                access.Error
            );
    }

    private static string ValidateName(string? value)
    {
        var name = value?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > 128)
            throw new ContractOperationException(
                400,
                "invalid_contract_name",
                "Contract name is required and must be at most 128 characters."
            );

        return name;
    }

    private DateTime GetStorageTime() =>
        DateTimeOffset
            .FromUnixTimeMilliseconds(timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
            .UtcDateTime;

    private static ContractOperationException DuplicateName() =>
        new(
            409,
            "duplicate_contract_name",
            "A contract with this name already exists in the project."
        );
}
