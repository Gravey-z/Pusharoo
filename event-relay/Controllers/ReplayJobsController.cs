using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Options;
using Pusharoo.EventRelay.Repositories;
using Pusharoo.EventRelay.Services;
namespace Pusharoo.EventRelay.Controllers;

[ApiController, RequireApiService, EnableRateLimiting("WebhookManagement")]
[Route("api/projects/{projectId}/subscriptions/{subscriptionId}/replays")]
public sealed class ReplayJobsController(IWebhookSubscriptionRepository subscriptions, ReplayJobRepository jobs,
    INeoRpcClient rpc, IOptions<NeoRpcOptions> rpcOptions, IOptions<EventRelayOptions> options) : ControllerBase
{
    private async Task<WebhookSubscriptionDocument?> Subscription(string project, string id, CancellationToken ct)
    {
        var sub = await subscriptions.GetByIdAsync(id, ct);
        return sub?.ProjectId == project && sub.Network == rpcOptions.Value.Network ? sub : null;
    }
    [HttpPost("query")]
    public async Task<IActionResult> List(string projectId, string subscriptionId, CancellationToken ct)
    {
        if (await Subscription(projectId, subscriptionId, ct) is null) return NotFound();
        return Ok(await jobs.ListAsync(projectId, subscriptionId, rpcOptions.Value.Network, ct));
    }
    [HttpPost]
    public async Task<IActionResult> Create(string projectId, string subscriptionId, CreateReplayRequest request, CancellationToken ct)
    {
        var sub = await Subscription(projectId, subscriptionId, ct);
        if (sub is null) return NotFound();
        if (!sub.IsEnabled) return BadRequest(new { error = "Resume the subscription before creating a replay." });
        if (request.ToBlock < request.FromBlock || request.ToBlock == uint.MaxValue
            || (ulong)request.ToBlock - request.FromBlock + 1 > (ulong)options.Value.ReplayMaxRangeBlocks)
            return BadRequest(new { error = $"Choose an inclusive range of at most {options.Value.ReplayMaxRangeBlocks} blocks." });
        uint count;
        try { await rpc.ValidateNetworkAsync(ct); count = await rpc.GetBlockCountAsync(ct); }
        catch (NeoRpcException) { return StatusCode(503, new { error = "Cannot validate the confirmed replay range; RPC is unavailable." }); }
        if (count == 0 || count - 1 < rpcOptions.Value.ConfirmationBlocks
            || request.ToBlock > count - 1 - rpcOptions.Value.ConfirmationBlocks)
            return BadRequest(new { error = "The replay range must contain confirmed blocks." });
        var job = new ReplayJobDocument { Id = Guid.NewGuid().ToString("N"), ProjectId = projectId, SubscriptionId = sub.Id,
            Network = sub.Network, ContractHash = sub.ContractHash, EventName = sub.EventName,
            FromBlock = request.FromBlock, ToBlock = request.ToBlock, NextBlock = request.FromBlock };
        try { await jobs.CreateAsync(job, ct); }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey) {
            return Conflict(new { error = "This subscription already has a running, paused or blocked replay." });
        }
        return Accepted(job);
    }
    [HttpPost("{jobId}/{action}")]
    public async Task<IActionResult> Action(string projectId, string subscriptionId, string jobId, string action, CancellationToken ct)
    {
        if (action is not ("pause" or "resume" or "cancel")) return NotFound();
        if (await Subscription(projectId, subscriptionId, ct) is not { } sub) return NotFound();
        var job = await jobs.GetAsync(jobId, ct);
        if (job?.ProjectId != projectId || job.SubscriptionId != subscriptionId || job.Network != rpcOptions.Value.Network) return NotFound();
        if (action == "resume" && !sub.IsEnabled) return BadRequest(new { error = "Resume the subscription first." });
        if (!await jobs.ActionAsync(jobId, action, ct)) return Conflict(new { error = "This action is not valid for the replay's current state." });
        return Ok(await jobs.GetAsync(jobId, ct));
    }
}
