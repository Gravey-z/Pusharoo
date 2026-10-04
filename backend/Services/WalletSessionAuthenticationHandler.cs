using System.Security.Claims;
using System.Globalization;
using System.Text.Encodings.Web;
using backend.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace backend.Services;

public sealed class WalletSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider services,
    WalletAuthCookies cookies)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PusharooWalletSession";
    public const string UnavailableItem = "PusharooWalletSessionUnavailable";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[cookies.SessionName];
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();
        try
        {
            var session = await services.GetRequiredService<WalletAuthService>()
                .GetSessionAsync(token, Context.RequestAborted);
            if (session is null) return AuthenticateResult.NoResult();
            return AuthenticateResult.Success(new AuthenticationTicket(CreatePrincipal(session), SchemeName));
        }
        catch (MongoException)
        {
            Context.Items[UnavailableItem] = true;
            return AuthenticateResult.Fail("Wallet session storage is unavailable.");
        }
        catch (TimeoutException)
        {
            Context.Items[UnavailableItem] = true;
            return AuthenticateResult.Fail("Wallet session storage is unavailable.");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => WriteError(Context.Items.ContainsKey(UnavailableItem) ? 503 : 401,
            Context.Items.ContainsKey(UnavailableItem) ? "auth_unavailable" : "login_required");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => WriteError(403, "forbidden");

    private Task WriteError(int status, string code)
    {
        Response.StatusCode = status;
        Response.Headers.CacheControl = "no-store";
        return Response.WriteAsJsonAsync(new { code, error = status == 503
            ? "Wallet authentication is temporarily unavailable."
            : status == 401 ? "Sign in to Pusharoo." : "This wallet is not permitted to perform this action." });
    }

    public static ClaimsPrincipal CreatePrincipal(WalletLoginSessionDocument session)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.Address),
            new Claim(ClaimTypes.Name, session.Address),
            new Claim("wallet_script_hash", session.ScriptHash),
            new Claim("wallet_public_key", session.PublicKey),
            new Claim("wallet_session_hash", session.TokenHash),
            new Claim("wallet_session_expires", session.ExpiresAtUtc.ToString("O"))
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
    }
}

public sealed class CurrentWalletSessionAccessor(IHttpContextAccessor httpContextAccessor)
{
    public WalletSessionIdentity? Current
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true
                || user.Identity.AuthenticationType != WalletSessionAuthenticationHandler.SchemeName) return null;
            var address = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var scriptHash = user.FindFirstValue("wallet_script_hash");
            var publicKey = user.FindFirstValue("wallet_public_key");
            var sessionHash = user.FindFirstValue("wallet_session_hash");
            var expiry = user.FindFirstValue("wallet_session_expires");
            return address is not null && scriptHash is not null && publicKey is not null
                && sessionHash is not null && DateTime.TryParseExact(expiry, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expiresAt)
                ? new WalletSessionIdentity(address, scriptHash, publicKey, sessionHash, expiresAt)
                : null;
        }
    }
}

public sealed record WalletSessionIdentity(string Address, string ScriptHash, string PublicKey,
    string SessionHash, DateTime ExpiresAtUtc);
