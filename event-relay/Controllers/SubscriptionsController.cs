using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;

namespace Pusharoo.EventRelay.Controllers;

[ApiController]
[RequireApiService]
[EnableRateLimiting("WebhookManagement")]
[Route("api/projects/{projectId}/subscriptions")]
public sealed class SubscriptionsController(SubscriptionManagementService management)
    : ControllerBase
{
    [HttpPost("query")]
    public async Task<ActionResult<IReadOnlyList<SubscriptionResponse>>> GetAll(
        string projectId,
        CancellationToken cancellationToken
    ) => Ok(await management.GetAllAsync(projectId, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<SubscriptionResponse>> Create(
        string projectId,
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await management.CreateAsync(projectId, request, cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetAll), new { projectId }, result.Value)
            : Failure(result);
    }

    [HttpPost("usage")]
    public async Task<IActionResult> Usage(string projectId, CancellationToken cancellationToken) =>
        Ok(await management.UsageAsync(projectId, cancellationToken));

    [HttpPut("{subscriptionId}")]
    public async Task<ActionResult<SubscriptionResponse>> Update(
        string projectId,
        string subscriptionId,
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await management.UpdateAsync(
            projectId,
            subscriptionId,
            request,
            cancellationToken
        );

        return result.IsSuccess ? Ok(result.Value) : Failure(result);
    }

    [HttpDelete("{subscriptionId}")]
    public async Task<IActionResult> Delete(
        string projectId,
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var result = await management.DeleteAsync(projectId, subscriptionId, cancellationToken);

        return result.IsSuccess ? NoContent() : Failure(result);
    }

    [HttpPost("{subscriptionId}/deliveries/query")]
    public async Task<ActionResult<IReadOnlyList<WebhookDeliveryDocument>>> GetDeliveries(
        string projectId,
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var result = await management.GetDeliveriesAsync(
            projectId,
            subscriptionId,
            cancellationToken
        );

        return result.IsSuccess ? Ok(result.Value) : Failure(result);
    }

    [HttpPost("{subscriptionId}/test")]
    public async Task<IActionResult> Test(
        string projectId,
        string subscriptionId,
        CancellationToken cancellationToken
    )
    {
        var result = await management.TestAsync(projectId, subscriptionId, cancellationToken);

        return result.IsSuccess ? Accepted(result.Value) : Failure(result);
    }

    [HttpPost("{subscriptionId}/deliveries/{deliveryId}/redeliver")]
    public async Task<IActionResult> Redeliver(
        string projectId,
        string subscriptionId,
        string deliveryId,
        CancellationToken cancellationToken
    )
    {
        var result = await management.RedeliverAsync(
            projectId,
            subscriptionId,
            deliveryId,
            cancellationToken
        );

        return result.IsSuccess ? Ok(result.Value) : Failure(result);
    }

    private ActionResult Failure<T>(SubscriptionManagementResult<T> result) =>
        result.Failure switch
        {
            SubscriptionManagementFailure.Invalid => BadRequest(new { error = result.Error }),
            SubscriptionManagementFailure.NotFound => NotFound(),
            SubscriptionManagementFailure.Conflict => Conflict(new { error = result.Error }),
            SubscriptionManagementFailure.Gone => StatusCode(
                StatusCodes.Status410Gone,
                new { error = result.Error }
            ),
            _ => throw new InvalidOperationException(
                "A failed subscription operation must specify a failure."
            ),
        };
}
