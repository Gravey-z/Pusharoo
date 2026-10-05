using System.Text.Json;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[RequireWalletSession]
[Route("api/projects/{projectId}/relay/{network}")]
public sealed class ProjectRelayGatewayController(
    ProjectService projects,
    ProjectAuthorizationService authorization,
    CurrentWalletSessionAccessor walletSession,
    RelayGatewayService relay) : ControllerBase
{
    [HttpPost("subscriptions/query")]
    public Task<IActionResult> Subscriptions(string projectId, string network, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post, "subscriptions/query", null, ct);

    [HttpPost("subscriptions/usage")]
    public Task<IActionResult> Usage(string projectId, string network, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post, "subscriptions/usage", null, ct);

    [HttpPost("subscriptions")]
    public Task<IActionResult> CreateSubscription(string projectId, string network, [FromBody] JsonElement body, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post, "subscriptions", body, ct);

    [HttpPut("subscriptions/{subscriptionId}")]
    public Task<IActionResult> UpdateSubscription(string projectId, string network, string subscriptionId,
        [FromBody] JsonElement body, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Put, $"subscriptions/{Uri.EscapeDataString(subscriptionId)}", body, ct);

    [HttpDelete("subscriptions/{subscriptionId}")]
    public Task<IActionResult> DeleteSubscription(string projectId, string network, string subscriptionId, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Delete, $"subscriptions/{Uri.EscapeDataString(subscriptionId)}", null, ct);

    [HttpPost("subscriptions/{subscriptionId}/deliveries/query")]
    public Task<IActionResult> Deliveries(string projectId, string network, string subscriptionId, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post, $"subscriptions/{Uri.EscapeDataString(subscriptionId)}/deliveries/query", null, ct);

    [HttpPost("subscriptions/{subscriptionId}/test")]
    public Task<IActionResult> Test(string projectId, string network, string subscriptionId, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post, $"subscriptions/{Uri.EscapeDataString(subscriptionId)}/test", null, ct);

    [HttpPost("subscriptions/{subscriptionId}/deliveries/{deliveryId}/redeliver")]
    public Task<IActionResult> Redeliver(string projectId, string network, string subscriptionId, string deliveryId, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post,
            $"subscriptions/{Uri.EscapeDataString(subscriptionId)}/deliveries/{Uri.EscapeDataString(deliveryId)}/redeliver", null, ct);

    [HttpPost("payments/intents")]
    public Task<IActionResult> CreatePaymentIntent(string projectId, string network, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post, "relay/payments/intents", null, ct);

    [HttpPost("payments/confirm")]
    public Task<IActionResult> ConfirmPayment(string projectId, string network, [FromBody] ConfirmRelayPaymentRequest body, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post, "relay/payments/confirm", body, ct);

    [HttpPost("payments/history/query")]
    public Task<IActionResult> PaymentHistory(string projectId, string network, CancellationToken ct)
        => Forward(projectId, network, HttpMethod.Post, "relay/payments/history/query", null, ct);

    private async Task<IActionResult> Forward(string projectId, string network, HttpMethod method,
        string resource, object? body, CancellationToken ct)
    {
        if (network is not ("testnet" or "mainnet")) return NotFound(new { error = "Relay network was not found." });
        if (projectId.Length != 24 || !projectId.All(Uri.IsHexDigit)
            || resource.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '/'))
            return BadRequest(new { error = "Relay resource identifier is invalid." });
        if (resource.StartsWith("relay/payments/", StringComparison.Ordinal) && network != "mainnet")
            return NotFound(new { error = "Relay payments are available on MainNet only." });
        var project = await projects.GetByIdAsync(projectId, ct);
        if (project is null) return NotFound(new { error = "Project was not found." });
        var actor = walletSession.Current!;
        var access = authorization.CanManageWebhooks(project, actor.Address);
        if (!access.IsAllowed) return StatusCode(access.StatusCode, new { error = access.Error });
        Response.Headers.CacheControl = "no-store";
        var result = await relay.SendAsync(network, method,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/{resource}", body, actor, ct);
        return new ContentResult { StatusCode = result.StatusCode,
            Content = result.StatusCode == StatusCodes.Status204NoContent ? null : result.Content,
            ContentType = "application/json" };
    }
}

public sealed record ConfirmRelayPaymentRequest(string IntentId, string TransactionId);
