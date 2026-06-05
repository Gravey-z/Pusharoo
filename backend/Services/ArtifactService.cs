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

    public async Task<ArtifactComparisonResponse?> CompareVersionsAsync(
        string projectId,
        string fromVersion,
        string toVersion,
        CancellationToken cancellationToken)
    {
        var fromArtifact = await GetByVersionAsync(projectId, fromVersion, cancellationToken);
        var toArtifact = await GetByVersionAsync(projectId, toVersion, cancellationToken);

        if (fromArtifact is null || toArtifact is null)
        {
            return null;
        }

        return CompareManifests(fromArtifact.Manifest, toArtifact.Manifest);
    }

    private static NeoContractManifest ParseManifest(string manifestJson)
    {
        return JsonSerializer.Deserialize<NeoContractManifest>(manifestJson, JsonOptions)
            ?? throw new JsonException("Could not parse manifest.");
    }

    private async Task<ArtifactDocument?> GetByVersionAsync(
        string projectId,
        string version,
        CancellationToken cancellationToken)
    {
        var trimmedVersion = version.Trim();
        var artifact = await artifacts.GetByProjectIdAndVersionAsync(projectId, trimmedVersion, cancellationToken);
        if (artifact is not null)
        {
            return artifact;
        }

        var alternateVersion = trimmedVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmedVersion[1..]
            : $"v{trimmedVersion}";

        return await artifacts.GetByProjectIdAndVersionAsync(projectId, alternateVersion, cancellationToken);
    }

    private static ArtifactComparisonResponse CompareManifests(
        NeoContractManifest fromManifest,
        NeoContractManifest toManifest)
    {
        var fromMethods = ToMethodMap(fromManifest.Abi.Methods);
        var toMethods = ToMethodMap(toManifest.Abi.Methods);

        var addedMethods = toMethods.Keys
            .Except(fromMethods.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var removedMethods = fromMethods.Keys
            .Except(toMethods.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var changedMethods = fromMethods.Keys
            .Intersect(toMethods.Keys, StringComparer.Ordinal)
            .Select(name => GetChangedMethod(name, fromMethods[name], toMethods[name]))
            .Where(change => change is not null)
            .Cast<ChangedMethodResponse>()
            .OrderBy(change => change.Name, StringComparer.Ordinal)
            .ToArray();

        var fromEventNames = fromManifest.Abi.Events
            .Select(contractEvent => contractEvent.Name)
            .ToHashSet(StringComparer.Ordinal);

        var addedEvents = toManifest.Abi.Events
            .Select(contractEvent => contractEvent.Name)
            .Where(name => !fromEventNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var permissionChanges = GetPermissionChanges(fromManifest.Permissions, toManifest.Permissions);

        return new ArtifactComparisonResponse(
            addedMethods,
            removedMethods,
            changedMethods,
            addedEvents,
            permissionChanges);
    }

    private static Dictionary<string, NeoMethod> ToMethodMap(IEnumerable<NeoMethod> methods)
    {
        return methods
            .GroupBy(method => method.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static ChangedMethodResponse? GetChangedMethod(
        string name,
        NeoMethod fromMethod,
        NeoMethod toMethod)
    {
        if (GetSignature(fromMethod) == GetSignature(toMethod))
        {
            return null;
        }

        var changes = new List<string>();

        var fromParameters = GetParameterSignature(fromMethod.Parameters);
        var toParameters = GetParameterSignature(toMethod.Parameters);
        if (fromParameters != toParameters)
        {
            changes.Add($"Parameters changed from {FormatParameters(fromMethod.Parameters)} to {FormatParameters(toMethod.Parameters)}");
        }

        if (!string.Equals(fromMethod.ReturnType, toMethod.ReturnType, StringComparison.Ordinal))
        {
            changes.Add($"Return type changed from {fromMethod.ReturnType} to {toMethod.ReturnType}");
        }

        if (fromMethod.Safe != toMethod.Safe)
        {
            changes.Add($"Safe flag changed from {FormatBoolean(fromMethod.Safe)} to {FormatBoolean(toMethod.Safe)}");
        }

        if (changes.Count == 0)
        {
            changes.Add("Method signature changed");
        }

        return new ChangedMethodResponse(name, changes);
    }

    private static string GetSignature(NeoMethod method)
    {
        return $"{method.Name}({GetParameterSignature(method.Parameters)}):{method.ReturnType}:safe={method.Safe}";
    }

    private static string GetParameterSignature(IEnumerable<NeoParameter> parameters)
    {
        return string.Join(
            ",",
            parameters.Select(parameter => $"{parameter.Name}:{parameter.Type}"));
    }

    private static string FormatParameters(IReadOnlyCollection<NeoParameter> parameters)
    {
        if (parameters.Count == 0)
        {
            return "-";
        }

        return string.Join(
            ", ",
            parameters.Select(parameter => $"{parameter.Name}: {parameter.Type}"));
    }

    private static string FormatBoolean(bool value)
    {
        return value ? "true" : "false";
    }

    private static IReadOnlyList<string> GetPermissionChanges(
        IReadOnlyCollection<NeoPermission> fromPermissions,
        IReadOnlyCollection<NeoPermission> toPermissions)
    {
        var fromPermissionMap = ToPermissionMap(fromPermissions);
        var toPermissionMap = ToPermissionMap(toPermissions);
        var changes = new List<string>();

        foreach (var addedPermission in toPermissionMap.Keys.Except(fromPermissionMap.Keys, StringComparer.Ordinal))
        {
            changes.Add(IsWildcardPermission(toPermissionMap[addedPermission])
                ? "Added wildcard permission"
                : $"Added permission {addedPermission}");
        }

        foreach (var removedPermission in fromPermissionMap.Keys.Except(toPermissionMap.Keys, StringComparer.Ordinal))
        {
            changes.Add($"Removed permission {removedPermission}");
        }

        return changes;
    }

    private static Dictionary<string, NeoPermission> ToPermissionMap(IEnumerable<NeoPermission> permissions)
    {
        return permissions
            .GroupBy(NormalizePermission, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static string NormalizePermission(NeoPermission permission)
    {
        return $"{NormalizeBsonValue(permission.Contract)}::{NormalizeBsonValue(permission.Methods)}";
    }

    private static bool IsWildcardPermission(NeoPermission permission)
    {
        return IsWildcardValue(permission.Contract) || IsWildcardValue(permission.Methods);
    }

    private static bool IsWildcardValue(BsonValue value)
    {
        return value switch
        {
            BsonString bsonString => bsonString.Value == "*",
            BsonArray bsonArray => bsonArray.Any(IsWildcardValue),
            _ => false
        };
    }

    private static string NormalizeBsonValue(BsonValue value)
    {
        return value switch
        {
            BsonDocument document => "{" + string.Join(
                ",",
                document.Elements
                    .OrderBy(element => element.Name, StringComparer.Ordinal)
                    .Select(element => $"{element.Name}:{NormalizeBsonValue(element.Value)}")) + "}",
            BsonArray array => "[" + string.Join(",", array.Select(NormalizeBsonValue)) + "]",
            BsonString bsonString => bsonString.Value,
            BsonNull => "null",
            _ => value.ToString() ?? string.Empty
        };
    }
}
