using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments.Settings;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>Deployment concurrency (concurrent builds, default 2) and queue limit (default 25) of a server.</summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers/{serverId:guid}/deployment-limits")]
public sealed class ServerDeploymentLimitsController(DeploymentSettingsService settings) : ControllerBase
{
    [HttpGet]
    public ActionResult<ServerDeploymentLimitsDto> Get(Guid serverId)
    {
        return Ok(settings.GetServerLimits(User.GetUserId(), User.GetTeamId(), new ServerId(serverId)));
    }

    [HttpPut]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerDeploymentLimitsDto>> Update(Guid serverId,
        ServerDeploymentLimitsDto request, CancellationToken cancellationToken)
    {
        return Ok(await settings.UpdateServerLimitsAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            request, cancellationToken));
    }
}
