using Microsoft.AspNetCore.DataProtection;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookSessionService(IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector protector = protectionProvider.CreateProtector("Pusharoo.WebhookSession.v1");

    public string Create(string projectId) => protector.Protect($"{projectId}\n{DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds()}");

    public bool IsValid(string? token, string projectId)
    {
        try
        {
            var parts = protector.Unprotect(token ?? string.Empty).Split('\n');
            return parts.Length == 2 && parts[0] == projectId
                && long.TryParse(parts[1], out var expiry)
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry;
        }
        catch { return false; }
    }
}
