using Microsoft.AspNetCore.DataProtection;

namespace Pusharoo.EventRelay.Services;

public sealed class WebhookSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private const string Prefix = "protected:v1:";
    private readonly IDataProtector _protector = dataProtectionProvider
        .CreateProtector("Pusharoo.EventRelay.WebhookSecrets.v1");

    public string? Protect(string? secret)
    {
        return string.IsNullOrWhiteSpace(secret) ? null : Prefix + _protector.Protect(secret.Trim());
    }

    public string? Unprotect(string? storedSecret)
    {
        if (string.IsNullOrWhiteSpace(storedSecret))
        {
            return null;
        }

        return storedSecret.StartsWith(Prefix, StringComparison.Ordinal)
            ? _protector.Unprotect(storedSecret[Prefix.Length..])
            : storedSecret;
    }
}
