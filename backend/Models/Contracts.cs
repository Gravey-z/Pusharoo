namespace backend.Models;

public sealed record CreateProjectRequest(
    string Name,
    string? Description,
    WalletSignatureRequest? Signature);

public sealed record WalletSignatureRequest(
    string Address,
    string ScriptHash,
    string Network,
    string Provider,
    string Origin,
    string IssuedAtUtc,
    string Nonce,
    string Message,
    string PublicKey,
    string Data,
    string? Salt,
    string? MessageHex);

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
    string? CreatedByWalletAddress,
    string? CreatorNetwork,
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

public sealed record ArtifactComparisonResponse(
    IReadOnlyList<string> AddedMethods,
    IReadOnlyList<string> RemovedMethods,
    IReadOnlyList<ChangedMethodResponse> ChangedMethods,
    IReadOnlyList<string> AddedEvents,
    IReadOnlyList<string> PermissionChanges);

public sealed record ChangedMethodResponse(
    string Name,
    IReadOnlyList<string> Changes);

public sealed record CreateDeploymentRequest(
    string ArtifactId,
    string Network,
    string? ContractHash,
    string? TransactionId,
    string DeployedBy,
    string? Notes);

public sealed record DeploymentResponse(
    string Id,
    string ProjectId,
    string ArtifactId,
    string Version,
    string Network,
    string? ContractHash,
    string? TransactionId,
    string DeployedBy,
    string? Notes,
    DateTime CreatedAt);
