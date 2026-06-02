using backend.Models;
using backend.Repositories;
using MongoDB.Bson;
using System.Text.Json;

namespace backend.Services;

public sealed class ArtifactService(IArtifactRepository artifacts)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new BsonValueJsonConverter(), new BsonDocumentJsonConverter() }
    };

    public async Task<ArtifactDocument> CreateAsync(
        ArtifactUploadInput upload,
        CancellationToken cancellationToken)
    {
        var manifest = ParseManifest(upload.ManifestJson);
        var summary = ArtifactSummary.FromManifest(manifest);
        var nefFileName = Path.GetFileName(upload.NefFileName);

        var artifact = new ArtifactDocument
        {
            Id = ObjectId.GenerateNewId().ToString(),
            ProjectId = upload.ProjectId,
            Version = upload.Version.Trim(),
            Notes = string.IsNullOrWhiteSpace(upload.Notes) ? null : upload.Notes.Trim(),
            ContractName = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(nefFileName) : manifest.Name,
            NefFileName = nefFileName,
            NefSize = upload.Nef.LongLength,
            Nef = upload.Nef,
            Manifest = manifest,
            Summary = summary,
            Warnings = [],
            CreatedAt = DateTime.UtcNow
        };

        await artifacts.InsertAsync(artifact, cancellationToken);

        return artifact;
    }

    public async Task<IReadOnlyList<ArtifactDocument>> GetByProjectIdAsync(string projectId, CancellationToken cancellationToken)
    {
        return await artifacts.GetByProjectIdAsync(projectId, cancellationToken);
    }

    public async Task<ArtifactDocument?> GetByIdAsync(string artifactId, CancellationToken cancellationToken)
    {
        return await artifacts.GetByIdAsync(artifactId, cancellationToken);
    }

    private static NeoContractManifest ParseManifest(string manifestJson)
    {
        return JsonSerializer.Deserialize<NeoContractManifest>(manifestJson, JsonOptions)
            ?? throw new JsonException("Could not parse manifest.");
    }
}
