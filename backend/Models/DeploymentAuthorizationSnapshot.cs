using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

/// <summary>
/// Immutable authorization context used by Stage 3 once deployment signatures and
/// short-lived attempt capabilities are enabled. Existing attempts deserialize with
/// a null snapshot and remain creator-only under the fail-closed ownership policy.
/// </summary>
public sealed record DeploymentAuthorizationSnapshot
{
    [BsonElement("initiatorWalletAddress")]
    public string InitiatorWalletAddress { get; init; } = string.Empty;

    [BsonElement("initiatorScriptHash")]
    public string? InitiatorScriptHash { get; init; }

    [BsonElement("grantRevision")]
    public long? GrantRevision { get; init; }

    [BsonElement("expectedDeploymentRevision")]
    public long ExpectedDeploymentRevision { get; init; }

    [BsonElement("expectedTargetContractHash")]
    public string? ExpectedTargetContractHash { get; init; }

    [BsonElement("artifactNefSha256")]
    public string? ArtifactNefSha256 { get; init; }

    [BsonElement("artifactManifestSha256")]
    public string? ArtifactManifestSha256 { get; init; }

    [BsonElement("authorizationMessageHash")]
    public string? AuthorizationMessageHash { get; init; }

    [BsonElement("authorizedAtUtc")]
    public DateTime? AuthorizedAtUtc { get; init; }
}
