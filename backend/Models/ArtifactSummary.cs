using MongoDB.Bson.Serialization.Attributes;

namespace backend.Models;

public sealed class ArtifactSummary
{
    [BsonElement("methodCount")]
    public int MethodCount { get; init; }

    [BsonElement("eventCount")]
    public int EventCount { get; init; }

    [BsonElement("permissionCount")]
    public int PermissionCount { get; init; }

    [BsonElement("supportedStandards")]
    public IReadOnlyList<string> SupportedStandards { get; init; } = [];

    public static ArtifactSummary FromManifest(NeoContractManifest manifest)
    {
        return new ArtifactSummary
        {
            MethodCount = manifest.Abi.Methods.Count,
            EventCount = manifest.Abi.Events.Count,
            PermissionCount = manifest.Permissions.Count,
            SupportedStandards = manifest.SupportedStandards
        };
    }
}
