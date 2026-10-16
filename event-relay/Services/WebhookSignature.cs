using System.Security.Cryptography;
using System.Text;

namespace Pusharoo.EventRelay.Services;

public static class WebhookSignature
{
    public static string Create(string secret, string payload) =>
        Create(secret, Encoding.UTF8.GetBytes(payload));

    public static string Create(string secret, ReadOnlySpan<byte> payload) =>
        $"sha256={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant()}";

    public static bool Verify(string secret, ReadOnlySpan<byte> rawBody, string? signature)
    {
        if (
            signature is null
            || !signature.StartsWith("sha256=", StringComparison.Ordinal)
            || signature.Length != 71
        )
            return false;

        byte[] supplied;

        try
        {
            supplied = Convert.FromHexString(signature.AsSpan(7));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody);

        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
