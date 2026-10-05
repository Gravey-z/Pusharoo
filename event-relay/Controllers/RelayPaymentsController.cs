using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Controllers;

[ApiController]
[RequireApiService]
[EnableRateLimiting("WebhookManagement")]
[Route("api/projects/{projectId}/relay/payments")]
public sealed class RelayPaymentsController(RelayPaymentService payments) : ControllerBase
{
    [HttpPost("intents")]
    public async Task<ActionResult<PaymentIntentResponse>> CreateIntent(string projectId, CancellationToken ct)
    {
        try
        {
            return Ok(await payments.CreateIntentAsync(projectId,
                Request.Headers["X-Pusharoo-Actor-Address"].ToString(),
                Request.Headers["X-Pusharoo-Actor-Script-Hash"].ToString(), ct));
        }
        catch (PaymentValidationException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
    }

    [HttpPost("confirm")]
    public async Task<ActionResult<PaymentResponse>> Confirm(string projectId, ConfirmPaymentRequest request, CancellationToken ct)
    {
        try { return Ok(await payments.ConfirmAsync(projectId, request.IntentId, request.TransactionId, ct)); }
        catch (PaymentValidationException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
    }

    [HttpPost("history/query")]
    public async Task<ActionResult<PaymentHistoryResponse>> History(string projectId, CancellationToken ct)
        => Ok(await payments.HistoryAsync(projectId, ct));
}
