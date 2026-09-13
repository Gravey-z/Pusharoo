namespace backend.Models;

public sealed record CreateProjectRequest(
    string Name,
    string? Description,
    WalletSignatureRequest? Signature);

public sealed record DeleteProjectRequest(
    string ProjectName,
    WalletSignatureRequest? Signature);

public sealed record WebhookAccessValidationRequest(
    string Operation,
    string RequestHash,
    WalletSignatureRequest? Signature);

public sealed record AddProjectAuthorizedDeployerRequest(
    string WalletAddress,
    IReadOnlyList<string>? AllowedNetworks,
    WalletSignatureRequest? Signature);

public sealed record UpdateProjectAuthorizedDeployerRequest(
    IReadOnlyList<string>? AllowedNetworks,
    WalletSignatureRequest? Signature);

public sealed record RemoveProjectAuthorizedDeployerRequest(
    WalletSignatureRequest? Signature);

public sealed record ProjectAuthorizedDeployerResponse(
    string WalletAddress,
    string ScriptHash,
    IReadOnlyList<string> AllowedNetworks,
    DateTime AddedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ArtifactUploadInput(
    string ProjectId,
    string Version,
    string? Notes,
    string NefFileName,
    byte[] Nef,
    string ManifestJson,
    string? IdempotencyKey);

public sealed record ProjectResponse(
    string Id,
    string Name,
    string? Description,
    string? CreatedByWalletAddress,
    string? CreatorNetwork,
    DateTime CreatedAt);

public sealed record ProjectListItemResponse(
    ProjectResponse Project,
    ProjectListArtifactResponse? LatestArtifact,
    IReadOnlyList<string> DeploymentNetworks,
    bool Deployed);

public sealed record ProjectListArtifactResponse(
    string Version,
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

public sealed record DeploymentAuthorizationChallengeRequest(
    string ArtifactId,
    string Network,
    string DeployedBy,
    string? Notes,
    string Origin,
    string Audience,
    string IssuedAtUtc,
    string Nonce);

public sealed record DeploymentAuthorizationChallengeResponse(
    string Message,
    string Operation,
    string? ExpectedTargetContractHash,
    long ExpectedDeploymentRevision);

public sealed record StartDeploymentAttemptRequest(
    string ArtifactId,
    string Network,
    string DeployedBy,
    string? Notes,
    WalletSignatureRequest? Authorization);

public sealed record SubmitDeploymentAttemptRequest(string TransactionId, string AttemptCapability);

public sealed record ConfirmDeploymentAttemptRequest(string AttemptCapability);

public sealed record FailDeploymentAttemptRequest(string AttemptCapability, string Stage, string Reason);

public sealed record RecoverDeploymentRequest(
    string ArtifactId,
    string Network,
    string TransactionId,
    string DeployedBy,
    string? Notes,
    WalletSignatureRequest? Authorization);

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
    DateTime CreatedAt,
    string Operation,
    string Status,
    string? FailureStage,
    string? FailureReason,
    DateTime UpdatedAt,
    string? AttemptCapability = null);
