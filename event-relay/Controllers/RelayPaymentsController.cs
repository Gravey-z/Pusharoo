using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Pusharoo.EventRelay.Controllers;

[ApiController]
[RequireApiService]
[EnableRateLimiting("WebhookManagement")]
[Route("api/projects/{projectId}/relay/payments")]
public sealed class RelayPaymentsController(RelayPaymentService payments) : ControllerBase
{
    [HttpPost("intents")]
    public async Task<ActionResult<PaymentIntentResponse>> CreateIntent(
        string projectId,
        CancellationToken ct,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? request = null
    )
    {
        try
        {
            if (request is { ValueKind: not JsonValueKind.Null })
                return Conflict(new { error = "Package-aware purchases and upgrades are not enabled yet. This endpoint currently supports the existing Starter purchase only." });
            return Ok(
                await payments.CreateIntentAsync(
                    projectId,
                    Request.Headers["X-Pusharoo-Actor-Address"].ToString(),
                    Request.Headers["X-Pusharoo-Actor-Script-Hash"].ToString(),
                    ct
                )
            );
        }
        catch (PaymentValidationException error)
        {
            return StatusCode(error.StatusCode, new { error = error.Message });
        }
    }

    [HttpPost("confirm")]
    public async Task<ActionResult<PaymentResponse>> Confirm(
        string projectId,
        ConfirmPaymentRequest request,
        CancellationToken ct
    )
    {
        try
        {
            return Ok(
                await payments.ConfirmAsync(projectId, request.IntentId, request.TransactionId, ct)
            );
        }
        catch (PaymentValidationException error)
        {
            return StatusCode(error.StatusCode, new { error = error.Message });
        }
    }

    [HttpPost("history/query")]
    public async Task<ActionResult<PaymentHistoryResponse>> History(
        string projectId,
        CancellationToken ct
    ) => Ok(await payments.HistoryAsync(projectId, ct));
}
