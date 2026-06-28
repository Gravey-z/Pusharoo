using System.Security.Cryptography;
using System.Text;

namespace Pusharoo.EventRelay.Services;

public static class WebhookSignature
{
    public static string Create(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(key, bytes);

        return $"sha256={Convert.ToHexString(signature).ToLowerInvariant()}";
    }
}
