using Microsoft.AspNetCore.DataProtection;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookSessionService(IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector protector = protectionProvider.CreateProtector("Pusharoo.WebhookSession.v1");

    public string Create(string projectId, string network) => protector.Protect(
        $"{projectId}\n{network}\n{DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds()}");

    public bool IsValid(string? token, string projectId, string network)
    {
        try
        {
            var parts = protector.Unprotect(token ?? string.Empty).Split('\n');
            return parts.Length == 3
                && string.Equals(parts[0], projectId, StringComparison.Ordinal)
                && string.Equals(parts[1], network, StringComparison.Ordinal)
                && long.TryParse(parts[2], out var expiry)
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry;
        }
        catch { return false; }
    }
}
