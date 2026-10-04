using backend.Options;
using Microsoft.Extensions.Options;

namespace backend.Services;

public sealed class WalletAuthCookies
{
    private readonly bool localHttp;

    public WalletAuthCookies(IOptions<WalletAuthOptions> options)
    {
        var config = options.Value;
        if (!string.IsNullOrWhiteSpace(config.PublicOrigin))
        {
            if (!Uri.TryCreate(config.PublicOrigin, UriKind.Absolute, out var uri)
                || !string.Equals(uri.GetLeftPart(UriPartial.Authority), config.PublicOrigin, StringComparison.Ordinal)
                || (uri.Scheme != Uri.UriSchemeHttps && !(config.AllowInsecureLocalhost
                    && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            {
                throw new InvalidOperationException("WalletAuth:PublicOrigin must be HTTPS, or explicitly allow HTTP on localhost.");
            }
            localHttp = uri.Scheme == Uri.UriSchemeHttp;
        }
    }

    public bool Secure => !localHttp;
    public string SessionName => localHttp ? "Pusharoo.Local.Session" : "__Host-Pusharoo.Session";
    public string ChallengeName => localHttp ? "Pusharoo.Local.LoginChallenge" : "__Host-Pusharoo.LoginChallenge";

    public CookieOptions SessionOptions(DateTime expiry) => new()
    {
        HttpOnly = true,
        Secure = Secure,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = new DateTimeOffset(expiry)
    };

    public CookieOptions ChallengeOptions(DateTime expiry) => new()
    {
        HttpOnly = true,
        Secure = Secure,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = new DateTimeOffset(expiry)
    };

    public CookieOptions ExpiredOptions() => new()
    {
        HttpOnly = true,
        Secure = Secure,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = DateTimeOffset.UnixEpoch
    };

    public CookieOptions XsrfOptions() => new()
    {
        HttpOnly = false,
        Secure = Secure,
        SameSite = SameSiteMode.Lax,
        Path = "/"
    };
}
