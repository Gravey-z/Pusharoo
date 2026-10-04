using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace backend.Services;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireWalletSessionAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (http.Items.ContainsKey(WalletSessionAuthenticationHandler.UnavailableItem))
        {
            context.Result = Error(503, "auth_unavailable", "Wallet authentication is temporarily unavailable.");
            return;
        }
        if (http.User.Identity?.IsAuthenticated != true
            || http.User.Identity.AuthenticationType != WalletSessionAuthenticationHandler.SchemeName)
        {
            context.Result = Error(401, "login_required", "Sign in to Pusharoo.");
            return;
        }
        var current = http.RequestServices.GetRequiredService<CurrentWalletSessionAccessor>().Current;
        var expectedWallet = http.Request.Headers["X-Pusharoo-Expected-Wallet"].ToString();
        if (current is null || !string.Equals(expectedWallet, current.Address, StringComparison.Ordinal))
        {
            context.Result = Error(403, "wallet_mismatch", "The connected wallet does not match the Pusharoo login session.");
            return;
        }
        try
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = Error(403, "antiforgery_failed", "Security token expired. Refresh the session and try again.");
        }
    }

    private static ObjectResult Error(int status, string code, string message) => new(new { code, error = message })
    {
        StatusCode = status
    };
}
