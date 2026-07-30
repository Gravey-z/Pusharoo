using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using backend.Models;

namespace backend.Services;

public sealed class ArtifactValidationException(string message) : Exception(message);

public sealed class ArtifactValidator
{
    private static readonly HashSet<string> ContractParameterTypes = new(StringComparer.Ordinal)
    {
        "Any", "Boolean", "Integer", "ByteArray", "String", "Hash160", "Hash256", "PublicKey",
        "Signature", "Array", "Map", "InteropInterface", "Void"
    };

    public void Validate(byte[] nef, string manifestJson, NeoContractManifest manifest)
    {
        ValidateNef(nef);
        ValidateManifestDocument(manifestJson);
        ValidateManifest(manifest);
    }

    private static void ValidateNef(byte[] nef)
    {
        const uint nef3Magic = 0x3346454e;
        if (nef.Length < 72 || BinaryPrimitives.ReadUInt32LittleEndian(nef) != nef3Magic)
        {
            throw new ArtifactValidationException("The NEF file is not a valid NEF3 artifact.");
        }

        var payload = nef.AsSpan(0, nef.Length - sizeof(uint));
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(nef.AsSpan(nef.Length - sizeof(uint)));
        var firstHash = SHA256.HashData(payload);
        var actualChecksum = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(firstHash));
        if (actualChecksum != expectedChecksum)
        {
            throw new ArtifactValidationException("The NEF checksum is invalid.");
        }
    }

    private static void ValidateManifestDocument(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArtifactValidationException("The manifest root must be a JSON object.");
        }

        var root = document.RootElement;
        foreach (var property in new[] { "name", "groups", "features", "supportedstandards", "abi", "permissions", "trusts", "extra" })
        {
            if (!root.TryGetProperty(property, out _))
            {
                throw new ArtifactValidationException($"The manifest is missing required property '{property}'.");
            }
        }

        if (root.GetProperty("name").ValueKind != JsonValueKind.String
            || root.GetProperty("abi").ValueKind != JsonValueKind.Object
            || !root.GetProperty("abi").TryGetProperty("methods", out var methods)
            || methods.ValueKind != JsonValueKind.Array
            || !root.GetProperty("abi").TryGetProperty("events", out var events)
            || events.ValueKind != JsonValueKind.Array
            || root.GetProperty("permissions").ValueKind != JsonValueKind.Array)
        {
            throw new ArtifactValidationException("The manifest ABI and permissions fields have invalid types.");
        }
    }

    private static void ValidateManifest(NeoContractManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128)
        {
            throw new ArtifactValidationException("The manifest contract name must be between 1 and 128 characters.");
        }

        ValidateMembers(manifest.Abi.Methods, "method", method => method.Name, method => method.ReturnType, method => method.Parameters);
        ValidateMembers(manifest.Abi.Events, "event", contractEvent => contractEvent.Name, _ => null, contractEvent => contractEvent.Parameters);
    }

    private static void ValidateMembers<T>(
        IEnumerable<T> members,
        string memberType,
        Func<T, string> getName,
        Func<T, string?> getReturnType,
        Func<T, IEnumerable<NeoParameter>> getParameters)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var name = getName(member);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !names.Add(name))
            {
                throw new ArtifactValidationException($"Each ABI {memberType} requires a unique name of at most 128 characters.");
            }

            var returnType = getReturnType(member);
            if (returnType is not null && !ContractParameterTypes.Contains(returnType))
            {
                throw new ArtifactValidationException($"ABI {memberType} '{name}' has an invalid return type.");
            }

            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in getParameters(member))
            {
                if (string.IsNullOrWhiteSpace(parameter.Name) || parameter.Name.Length > 128 || !parameterNames.Add(parameter.Name)
                    || !ContractParameterTypes.Contains(parameter.Type) || parameter.Type == "Void")
                {
                    throw new ArtifactValidationException($"ABI {memberType} '{name}' has an invalid parameter.");
                }
            }
        }
    }
}
