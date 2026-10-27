using backend.Models;

namespace backend.Services;

public sealed class ArtifactUploadReader
{
    public async Task<ArtifactUploadInput> ReadAsync(
        HttpRequest request,
        string projectId,
        string? contractId,
        string walletAddress,
        CancellationToken cancellationToken
    )
    {
        if (!request.HasFormContentType)
            throw new ContractOperationException(
                415,
                "invalid_content_type",
                "Expected multipart/form-data."
            );

        var form = await request.ReadFormAsync(cancellationToken);
        var version = form["version"].FirstOrDefault();
        var notes = form["notes"].FirstOrDefault();

        if (
            string.IsNullOrWhiteSpace(version)
            || version.Trim().Length > 64
            || string.IsNullOrEmpty(ArtifactService.NormalizeVersion(version))
        )
            throw Invalid("Version is required and must be at most 64 characters.");

        if (notes?.Length > 2_000)
            throw Invalid("Notes must be at most 2000 characters.");

        var key = request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (
            request.Headers.ContainsKey("Idempotency-Key")
            && (string.IsNullOrEmpty(key) || key.Length > 128)
        )
            throw Invalid("Idempotency-Key must be between 1 and 128 characters.");

        string? scopedKey = null;
        if (key is not null)
        {
            scopedKey = contractId is null
                ? WorkspaceIdempotency.Scope(walletAddress, projectId, "artifact.upload", key)
                : WorkspaceIdempotency.Scope(
                    walletAddress,
                    projectId,
                    contractId,
                    "artifact.upload",
                    key
                );
        }

        var nef = form.Files.FirstOrDefault(file =>
            file.FileName.EndsWith(".nef", StringComparison.OrdinalIgnoreCase)
        );
        var manifest =
            form.Files.FirstOrDefault(file =>
                file.FileName.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase)
            )
            ?? form.Files.FirstOrDefault(file =>
                file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            );

        if (nef is null)
            throw Invalid("A .nef file is required.");
        if (manifest is null)
            throw Invalid("A contract manifest JSON file is required.");
        if (nef.Length is 0 or > 8 * 1024 * 1024 || manifest.Length is 0 or > 1024 * 1024)
            throw Invalid("NEF files must be at most 8 MB and manifest files at most 1 MB.");

        await using var nefStream = nef.OpenReadStream();
        using var buffer = new MemoryStream();
        await nefStream.CopyToAsync(buffer, cancellationToken);
        var nefBytes = buffer.ToArray();
        await using var manifestStream = manifest.OpenReadStream();
        using var reader = new StreamReader(manifestStream);
        var manifestJson = await reader.ReadToEndAsync(cancellationToken);
        var normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        var payloadHash = contractId is null
            ? WorkspaceIdempotency.Hash(
                version.Trim(),
                normalizedNotes,
                Path.GetFileName(nef.FileName),
                WorkspaceIdempotency.HashBytes(nefBytes),
                manifestJson
            )
            : WorkspaceIdempotency.Hash(
                projectId,
                contractId,
                ArtifactService.NormalizeVersion(version),
                normalizedNotes,
                Path.GetFileName(nef.FileName),
                WorkspaceIdempotency.HashBytes(nefBytes),
                manifestJson
            );

        return new ArtifactUploadInput(
            projectId,
            version,
            notes,
            nef.FileName,
            nefBytes,
            manifestJson,
            scopedKey,
            payloadHash,
            contractId
        );
    }

    private static ContractOperationException Invalid(string message) =>
        new(400, "invalid_artifact_upload", message);
}
