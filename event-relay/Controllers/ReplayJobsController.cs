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
public sealed class ReplayJobsController(
    IWebhookSubscriptionRepository subscriptions,
    ReplayJobRepository jobs,
    ReplayAdmissionService admissions,
    INeoRpcClient rpc,
    IOptions<NeoRpcOptions> rpcOptions,
    IOptions<EventRelayOptions> options,
    TimeProvider? timeProvider = null
) : ControllerBase
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private async Task<WebhookSubscriptionDocument?> Subscription(
        string project,
        string id,
        CancellationToken ct
    )
    {
        var sub = await subscriptions.GetByIdAsync(id, ct);

        return sub?.ProjectId == project && sub.Network == rpcOptions.Value.Network ? sub : null;
    }

    [HttpPost("query")]
    public async Task<IActionResult> List(
        string projectId,
        string subscriptionId,
        CancellationToken ct
    )
    {
        if (await Subscription(projectId, subscriptionId, ct) is null)
            return NotFound();

        return Ok(await jobs.ListAsync(projectId, subscriptionId, rpcOptions.Value.Network, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        string projectId,
        string subscriptionId,
        CreateReplayRequest request,
        CancellationToken ct
    )
    {
        var sub = await Subscription(projectId, subscriptionId, ct);

        if (sub is null)
            return NotFound();

        if (!sub.IsEnabled)
            return BadRequest(new { error = "Resume the subscription before creating a replay." });

        return await AdmitAsync(
            new ReplayJobDocument
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                UpdatedAt = clock.GetUtcNow().UtcDateTime,
                NextAttemptAt = clock.GetUtcNow().UtcDateTime,
                ProjectId = projectId,
                SubscriptionId = sub.Id,
                Network = sub.Network,
                ContractHash = sub.ContractHash,
                EventName = sub.EventName,
                FromBlock = request.FromBlock,
                ToBlock = request.ToBlock,
                NextBlock = request.FromBlock,
            },
            ct
        );
    }

    private async Task<IActionResult> AdmitAsync(ReplayJobDocument job, CancellationToken ct)
    {
        if (
            job.ToBlock < job.FromBlock
            || job.ToBlock == uint.MaxValue
            || (ulong)job.ToBlock - job.FromBlock + 1 > (ulong)options.Value.ReplayMaxRangeBlocks
        )
            return BadRequest(
                new
                {
                    error = $"Choose an inclusive range of at most {options.Value.ReplayMaxRangeBlocks} blocks.",
                }
            );

        uint count;

        try
        {
            await rpc.ValidateNetworkAsync(ct);
            count = await rpc.GetBlockCountAsync(ct);
        }
        catch (NeoRpcException)
        {
            return StatusCode(
                503,
                new { error = "Cannot validate the confirmed replay range; RPC is unavailable." }
            );
        }

        if (
            count == 0
            || count - 1 < rpcOptions.Value.ConfirmationBlocks
            || job.ToBlock > count - 1 - rpcOptions.Value.ConfirmationBlocks
        )
            return BadRequest(new { error = "The replay range must contain confirmed blocks." });

        try
        {
            var admission = await admissions.TryCreateAsync(job, ct);

            if (!admission.Accepted)
            {
                if (admission.StatusCode == 409)
                    return Conflict(new { error = admission.Error });

                if (admission.RetryAt is { } retry && ControllerContext.HttpContext is not null)
                    Response.Headers.RetryAfter = retry.ToString("R");

                return StatusCode(
                    admission.StatusCode,
                    new { error = admission.Error, retryAt = admission.RetryAt }
                );
            }
        }
        catch (MongoWriteException ex)
            when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(
                new { error = "This subscription already has a running, paused or blocked replay." }
            );
        }

        return Accepted(job);
    }

    [HttpPost("~/api/projects/{projectId}/replays/query")]
    public async Task<IActionResult> ListProject(string projectId, CancellationToken ct) =>
        Ok(await jobs.ListProjectAsync(projectId, rpcOptions.Value.Network, ct));

    [HttpPost("~/api/projects/{projectId}/replays")]
    public async Task<IActionResult> CreateProject(
        string projectId,
        CreateProjectReplayRequest request,
        CancellationToken ct
    )
    {
        var hash = request.ContractHash?.Trim().ToLowerInvariant();

        if (
            hash is null
            || hash.Length != 42
            || !hash.StartsWith("0x", StringComparison.Ordinal)
            || hash[2..].Any(x => !Uri.IsHexDigit(x))
        )
            return BadRequest(new { error = "Choose a valid contract address." });

        if (
            request.EventNames is not { Length: > 0 and <= 64 }
            || request.EventNames.Any(x =>
                string.IsNullOrWhiteSpace(x) || x.Length > 128 || x != x.Trim()
            )
        )
            return BadRequest(new { error = "Choose between 1 and 64 event types to recover." });

        var names = request
            .EventNames.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var targets = (await subscriptions.GetByProjectIdAsync(projectId, ct))
            .Where(x =>
                x.Network == rpcOptions.Value.Network
                && x.IsEnabled
                && x.ContractHash == hash
                && (x.EventName is null || names.Contains(x.EventName, StringComparer.Ordinal))
            )
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();

        if (
            targets.Length == 0
            || names.Any(name => !targets.Any(x => x.EventName is null || x.EventName == name))
        )
            return BadRequest(
                new
                {
                    error = "Every selected event needs an enabled matching webhook for this contract and network. Create or resume those webhooks first.",
                }
            );

        if (targets.Length > 20)
            return BadRequest(new { error = "A recovery can target at most 20 webhooks." });

        return await AdmitAsync(
            new ReplayJobDocument
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                UpdatedAt = clock.GetUtcNow().UtcDateTime,
                NextAttemptAt = clock.GetUtcNow().UtcDateTime,
                ProjectId = projectId,
                SubscriptionId = targets[0].Id,
                Network = rpcOptions.Value.Network,
                ContractHash = hash,
                SelectedEventNames = names,
                Targets = targets.Select(x => new ReplayTarget(x.Id, x.EventName)).ToArray(),
                FromBlock = request.FromBlock,
                ToBlock = request.ToBlock,
                NextBlock = request.FromBlock,
            },
            ct
        );
    }

    [HttpPost("~/api/projects/{projectId}/replays/{jobId}/{action}")]
    public async Task<IActionResult> ProjectAction(
        string projectId,
        string jobId,
        string action,
        CancellationToken ct
    )
    {
        if (action is not ("pause" or "resume" or "cancel"))
            return NotFound();

        var job = await jobs.GetAsync(jobId, ct);

        if (job?.ProjectId != projectId || job.Network != rpcOptions.Value.Network)
            return NotFound();

        if (action == "resume")
        {
            var targets = job.Targets ?? [new ReplayTarget(job.SubscriptionId, job.EventName)];

            foreach (var target in targets)
            {
                var sub = await Subscription(projectId, target.SubscriptionId, ct);

                if (
                    sub is null
                    || !sub.IsEnabled
                    || sub.ContractHash != job.ContractHash
                    || sub.EventName != target.EventName
                )
                    return BadRequest(
                        new
                        {
                            error = "All recovery webhooks must exist, be enabled and keep their original contract/event filters before resuming.",
                        }
                    );
            }
        }

        if (!await jobs.ActionAsync(jobId, action, ct))
            return Conflict(
                new { error = "This action is not valid for the replay's current state." }
            );

        return Ok(await jobs.GetAsync(jobId, ct));
    }

    [HttpPost("{jobId}/{action}")]
    public async Task<IActionResult> Action(
        string projectId,
        string subscriptionId,
        string jobId,
        string action,
        CancellationToken ct
    )
    {
        if (action is not ("pause" or "resume" or "cancel"))
            return NotFound();

        if (await Subscription(projectId, subscriptionId, ct) is not { } sub)
            return NotFound();

        var job = await jobs.GetAsync(jobId, ct);

        if (
            job?.ProjectId != projectId
            || job.SubscriptionId != subscriptionId
            || job.Network != rpcOptions.Value.Network
        )
            return NotFound();

        if (action == "resume" && !sub.IsEnabled)
            return BadRequest(new { error = "Resume the subscription first." });

        if (!await jobs.ActionAsync(jobId, action, ct))
            return Conflict(
                new { error = "This action is not valid for the replay's current state." }
            );

        return Ok(await jobs.GetAsync(jobId, ct));
    }
}
