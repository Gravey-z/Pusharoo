using System.Security.Claims;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Driver;

namespace backend.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class WalletAuthController(
    WalletAuthService auth,
    WalletAuthCookies cookies,
    CurrentWalletSessionAccessor currentWallet,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("session")]
    public IActionResult SessionStatus()
    {
        NoStore();
        if (HttpContext.Items.ContainsKey(WalletSessionAuthenticationHandler.UnavailableItem))
            return StatusCode(503, new { code = "auth_unavailable", error = "Wallet authentication is temporarily unavailable." });

        RefreshAntiforgery();
        var current = currentWallet.Current;
        return Ok(current is null
            ? new WalletSessionResponse(false, null, null, null, null)
            : new WalletSessionResponse(true, current.Address, current.ScriptHash, current.PublicKey, current.ExpiresAtUtc));
    }

    [HttpPost("challenges")]
    [EnableRateLimiting("WalletAuthChallenge")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Challenge([FromBody] WalletLoginChallengeRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            auth.RequireOrigin(Request.Headers.Origin.ToString());
            if (!await ValidateAntiforgeryAsync()) return AntiforgeryFailure();
            var (response, browserToken) = await auth.CreateChallengeAsync(request, Request.Headers.Origin.ToString(), cancellationToken);
            Response.Cookies.Append(cookies.ChallengeName, browserToken, cookies.ChallengeOptions(response.ExpiresAtUtc));
            return Ok(response);
        }
        catch (WalletAuthException error) { return AuthFailure(error); }
        catch (MongoException) { return StorageUnavailable(); }
        catch (TimeoutException) { return StorageUnavailable(); }
    }

    [HttpPost("login")]
    [EnableRateLimiting("WalletAuthLogin")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Login([FromBody] WalletLoginRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            auth.RequireOrigin(Request.Headers.Origin.ToString());
            if (!await ValidateAntiforgeryAsync()) return AntiforgeryFailure();
            var (session, token) = await auth.LoginAsync(request,
                Request.Cookies[cookies.ChallengeName], Request.Headers.Origin.ToString(),
                Request.Cookies[cookies.SessionName], cancellationToken);
            Response.Cookies.Append(cookies.SessionName, token, cookies.SessionOptions(session.ExpiresAtUtc));
            Response.Cookies.Delete(cookies.ChallengeName, cookies.ExpiredOptions());
            HttpContext.User = WalletSessionAuthenticationHandler.CreatePrincipal(session);
            RefreshAntiforgery();
            return Ok(new WalletSessionResponse(true, session.Address, session.ScriptHash,
                session.PublicKey, session.ExpiresAtUtc));
        }
        catch (WalletAuthException error) { return AuthFailure(error); }
        catch (MongoException) { return StorageUnavailable(); }
        catch (TimeoutException) { return StorageUnavailable(); }
    }

    [HttpPost("logout")]
    [EnableRateLimiting("WalletAuthLogin")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            auth.RequireOrigin(Request.Headers.Origin.ToString());
            if (User.Identity?.IsAuthenticated == true && !await ValidateAntiforgeryAsync())
                return AntiforgeryFailure();
            await auth.RevokeAsync(Request.Cookies[cookies.SessionName], cancellationToken);
            Response.Cookies.Delete(cookies.SessionName, cookies.ExpiredOptions());
            Response.Cookies.Delete(cookies.ChallengeName, cookies.ExpiredOptions());
            HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            RefreshAntiforgery();
            return Ok(new WalletSessionResponse(false, null, null, null, null));
        }
        catch (WalletAuthException error) { return AuthFailure(error); }
        catch (MongoException) { return StorageUnavailable(); }
        catch (TimeoutException) { return StorageUnavailable(); }
    }

    private async Task<bool> ValidateAntiforgeryAsync()
    {
        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
            return true;
        }
        catch (AntiforgeryValidationException) { return false; }
    }

    private void RefreshAntiforgery()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, cookies.XsrfOptions());
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
    private IActionResult AntiforgeryFailure() => StatusCode(403,
        new { code = "antiforgery_failed", error = "Security token expired. Refresh the session and try again." });
    private IActionResult AuthFailure(WalletAuthException error) => StatusCode(error.StatusCode,
        new { code = error.Code, error = error.Message });
    private IActionResult StorageUnavailable() => StatusCode(503,
        new { code = "auth_unavailable", error = "Wallet authentication is temporarily unavailable." });
}
