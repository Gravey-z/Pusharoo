using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;


public sealed record DeploymentAuthorizationSnapshot
{
    [BsonElement("initiatorWalletAddress")]
    public string InitiatorWalletAddress { get; init; } = string.Empty;

    [BsonElement("initiatorScriptHash")]
    public string? InitiatorScriptHash { get; init; }

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

    [BsonElement("authorizationSchemaVersion")]
    [BsonIgnoreIfNull]
    public int? AuthorizationSchemaVersion { get; init; }

    [BsonElement("deploymentData")]
    [BsonIgnoreIfNull]
    public DeploymentDataValue? DeploymentData { get; init; }

    [BsonElement("deploymentDataSha256")]
    [BsonIgnoreIfNull]
    public string? DeploymentDataSha256 { get; init; }

    [BsonElement("deploymentDataFormatVersion")]
    [BsonIgnoreIfNull]
    public string? DeploymentDataFormatVersion { get; init; }
}
