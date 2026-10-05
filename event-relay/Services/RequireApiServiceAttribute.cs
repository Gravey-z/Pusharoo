using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Options;

namespace Pusharoo.EventRelay.Services;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireApiServiceAttribute : Attribute, IAuthorizationFilter
{
    private static readonly Regex ScriptHash = new("^(?:0x)?[0-9a-fA-F]{40}$", RegexOptions.Compiled);

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var configured = context.HttpContext.RequestServices.GetRequiredService<IOptions<PusharooApiOptions>>().Value.ServiceToken;
        var supplied = context.HttpContext.Request.Headers["X-Pusharoo-Service-Token"].ToString();
        if (string.IsNullOrWhiteSpace(configured) || configured.Length < 32)
        {
            context.Result = new ObjectResult(new { error = "Relay API authentication is not configured." })
                { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }
        if (supplied.Length != configured.Length
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured)))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Relay management requires the Pusharoo API." });
            return;
        }
        var address = context.HttpContext.Request.Headers["X-Pusharoo-Actor-Address"].ToString();
        var scriptHash = context.HttpContext.Request.Headers["X-Pusharoo-Actor-Script-Hash"].ToString();
        if (address.Length != 34 || !address.StartsWith('N') || !ScriptHash.IsMatch(scriptHash))
        {
            context.Result = new BadRequestObjectResult(new { error = "Verified actor identity is missing." });
        }
    }
}
