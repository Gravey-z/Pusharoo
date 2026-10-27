using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

namespace Pusharoo.Migration;

public static class MigrationIdentity
{
    private static readonly string[] MutableFields =
    [
        "contractId",
        "legacyContractMappingVerified",
        "contractSchemaVersion",
        "contractMigrationState",
    ];

    public static string ContractId(string projectId, string key) =>
        Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"pusharoo:contract-migration:v1:{projectId.Length}:{projectId}:{key}"
                )
            )
        )[..24];

    public static string Hash(BsonDocument source)
    {
        var preserved = source.DeepClone().AsBsonDocument;
        foreach (var field in MutableFields)
            preserved.Remove(field);
        return Convert.ToHexStringLower(SHA256.HashData(preserved.ToBson()));
    }

    public static string? Text(BsonDocument source, string field) =>
        source.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;

    public static string Id(BsonDocument source) => Text(source, "_id") ?? "";

    public static string Parent(BsonDocument source) => Text(source, "projectId") ?? "";

    public static string NormalizeHash(string hash) =>
        hash.Trim()
            .Replace("0X", "0x", StringComparison.Ordinal)
            .Replace("0x", "", StringComparison.Ordinal)
            .ToLowerInvariant();
}
