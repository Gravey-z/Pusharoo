using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace backend.Services;

public static class WorkspaceIdempotency
{
    public static string Scope(string actor, string project, string action, string key)
        => Hash(actor, project, action, key);

    public static string Hash(params string?[] parts)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
