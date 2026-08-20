using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Controllers;

[ApiController]
[EnableRateLimiting("WebhookManagement")]
[Route("api/projects/{projectId}/relay/payments")]
public sealed class RelayPaymentsController(RelayPaymentService payments, WebhookSessionService sessions, ProjectAccessClient projectAccess, IOptions<NeoRpcOptions> neoRpcOptions) : ControllerBase
{
    [HttpPost("intents")]
    public async Task<ActionResult<PaymentIntentResponse>> CreateIntent(string projectId, CreatePaymentIntentRequest request, CancellationToken ct)
    {
        if (request.Signature is null) return Unauthorized(new { error = "Approve the payment intent with the project owner wallet." });
        var result = await projectAccess.ValidateAsync(projectId, "payments.create", WebhookManagementRequestHasher.HashPaymentIntent(projectId), request.Signature, ct);
        if (!result.IsAllowed) return StatusCode(result.StatusCode, new { error = result.Error });
        Response.Headers.Append("X-Pusharoo-Webhook-Session", sessions.Create(projectId, neoRpcOptions.Value.Network));
        try { return Ok(await payments.CreateIntentAsync(projectId, request.Signature, ct)); }
        catch (PaymentValidationException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
    }

    [HttpPost("confirm")]
    public async Task<ActionResult<PaymentResponse>> Confirm(string projectId, ConfirmPaymentRequest request, CancellationToken ct)
    {
        var authorization = await AuthorizeAsync(projectId, "payments.confirm", WebhookManagementRequestHasher.HashPaymentConfirmation(projectId, request.IntentId, request.TransactionId), request.Signature, ct);
        if (authorization is not null) return authorization;
        try { return Ok(await payments.ConfirmAsync(projectId, request.IntentId, request.TransactionId, ct)); }
        catch (PaymentValidationException error) { return StatusCode(error.StatusCode, new { error = error.Message }); }
    }

    [HttpPost("history/query")]
    public async Task<ActionResult<PaymentHistoryResponse>> History(string projectId, WebhookAccessRequest request, CancellationToken ct)
    {
        var authorization = await AuthorizeAsync(projectId, "payments.read", WebhookManagementRequestHasher.Hash(projectId, "payments.read"), request.Signature, ct);
        if (authorization is not null) return authorization;
        return Ok(await payments.HistoryAsync(projectId, ct));
    }

    private async Task<ActionResult?> AuthorizeAsync(string projectId, string operation, string requestHash, WalletSignatureRequest? signature, CancellationToken ct)
    {
        if (Request.Headers.TryGetValue("X-Pusharoo-Webhook-Session", out var session) && sessions.IsValid(session.ToString(), projectId, neoRpcOptions.Value.Network)) return null;
        if (Request.Headers.ContainsKey("X-Pusharoo-Webhook-Session") && signature is null) return Unauthorized(new { error = "Webhook session expired. Approve this action again to continue." });
        var result = await projectAccess.ValidateAsync(projectId, operation, requestHash, signature, ct);
        if (!result.IsAllowed) return StatusCode(result.StatusCode, new { error = result.Error });
        Response.Headers.Append("X-Pusharoo-Webhook-Session", sessions.Create(projectId, neoRpcOptions.Value.Network));
        return null;
    }
}
