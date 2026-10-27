using System.Text.Json;
using backend.Models;
using backend.Repositories;
using MongoDB.Driver;

namespace backend.Services;

public sealed class ArtifactUploadService(ArtifactService artifacts, IArtifactRepository repository)
{
    public async Task<ArtifactUploadResult> SaveAsync(
        ArtifactUploadInput input,
        CancellationToken cancellationToken
    )
    {
        if (
            input.ContractId is null
            && await repository.HasContractArtifactsAsync(input.ProjectId, cancellationToken)
        )
            throw new ContractOperationException(
                409,
                "contract_required",
                "Choose a contract to upload an artifact."
            );

        var existing = await FindRetryAsync(input, cancellationToken);
        if (existing is not null)
            return new(existing, false);

        try
        {
            return new(await artifacts.CreateAsync(input, cancellationToken), true);
        }
        catch (JsonException)
        {
            throw new ContractOperationException(
                400,
                "invalid_manifest",
                "The manifest file must contain valid JSON."
            );
        }
        catch (ArtifactValidationException error)
        {
            throw new ContractOperationException(400, "invalid_artifact", error.Message);
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            existing = await FindRetryAsync(input, cancellationToken);
            if (existing is not null)
                return new(existing, false);

            throw new ContractOperationException(
                409,
                "duplicate_artifact_version",
                "An artifact with this version already exists for the selected contract or legacy project."
            );
        }
    }

    private async Task<ArtifactDocument?> FindRetryAsync(
        ArtifactUploadInput input,
        CancellationToken cancellationToken
    )
    {
        if (input.IdempotencyKey is null)
            return null;

        var existing = await artifacts.GetByIdempotencyKeyAsync(
            input.IdempotencyKey,
            cancellationToken
        );
        if (existing is null)
            return null;

        if (
            existing.ProjectId != input.ProjectId
            || existing.ContractId != input.ContractId
            || existing.IdempotencyPayloadHash != input.IdempotencyPayloadHash
        )
            throw new ContractOperationException(
                409,
                "idempotency_conflict",
                "Idempotency-Key was already used with different artifact data."
            );

        return existing;
    }
}

public sealed record ArtifactUploadResult(ArtifactDocument Artifact, bool Created);
