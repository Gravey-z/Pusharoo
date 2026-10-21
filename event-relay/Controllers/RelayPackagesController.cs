using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pusharoo.EventRelay.Models;
using Pusharoo.EventRelay.Services;
using Pusharoo.EventRelay.Startup;

namespace Pusharoo.EventRelay.Controllers;

[ApiController]
[RequireApiService]
[EnableRateLimiting(RelayRateLimiting.Queries)]
[Route("api/projects/{projectId}/relay/packages")]
public sealed class RelayPackagesController(RelayPackageStore packages) : ControllerBase
{
    [HttpPost("query")]
    public async Task<ActionResult<RelayCatalogResponse>> Query(
        string projectId,
        CancellationToken ct
    )
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await packages.DescribeAsync(ct));
    }
}
