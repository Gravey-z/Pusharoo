using backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;

namespace backend.Controllers;

[ApiController]
[Route("api/faucet")]
[EnableRateLimiting("FaucetIp")]
public sealed class FaucetController(FaucetService faucet, CurrentWalletSessionAccessor currentWallet, FaucetClientIpResolver clientIpResolver) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<FaucetStatusResponse>> GetStatus([FromQuery] string? address, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await faucet.GetStatusAsync(address, cancellationToken));
        }
        catch (FaucetUnavailableException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Testnet faucet is temporarily unavailable." });
        }
    }

    [HttpPost("claims")]
    [RequireWalletSession]
    public async Task<ActionResult<FaucetClaimResponse>> SubmitClaim(
        [FromBody] FaucetClaimRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Accepted(await faucet.SubmitClaimAsync(request, currentWallet.Current!, clientIpResolver.Resolve(HttpContext), cancellationToken));
        }
        catch (FaucetRequestException exception)
        {
            return StatusCode(exception.StatusCode, new { error = exception.Message });
        }
        catch (FaucetUnavailableException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Testnet faucet is temporarily unavailable." });
        }
    }

    [HttpGet("claims/{requestId}")]
    public async Task<ActionResult<FaucetClaimResponse>> GetClaim(string requestId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var claim = await faucet.GetClaimAsync(requestId, cancellationToken);
        return claim is null ? NotFound() : Ok(claim);
    }
}
