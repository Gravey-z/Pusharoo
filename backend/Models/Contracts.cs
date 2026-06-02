namespace backend.Models;

public sealed record CreateProjectRequest(string Name, string? Description);

public sealed record ArtifactUploadInput(
    string ProjectId,
    string Version,
    string? Notes,
    string NefFileName,
    byte[] Nef,
    string ManifestJson);

public sealed record ProjectResponse(
    string Id,
    string Name,
    string? Description,
    DateTime CreatedAt);

public sealed record ArtifactResponse(
    string Id,
    string ProjectId,
    string Version,
    string? Notes,
    string ContractName,
    string NefFileName,
    long NefSize,
    NeoContractManifest Manifest,
    ArtifactSummary Summary,
    IReadOnlyList<string> Warnings,
    DateTime CreatedAt);
