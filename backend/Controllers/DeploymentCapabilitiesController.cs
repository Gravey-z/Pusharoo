using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/deployment-capabilities")]
public sealed class DeploymentCapabilitiesController(DeploymentCapabilityService capabilities) : ControllerBase
{
    [HttpGet]
    public ActionResult<DeploymentCapabilitiesResponse> Get()
        => Ok(capabilities.GetCapabilities());
}
