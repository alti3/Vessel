using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers/{serverId:guid}/proxy")]
public sealed class ServerProxyController(ProxyLifecycleService proxyLifecycle) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ServerProxySummary>> Get(Guid serverId, CancellationToken cancellationToken)
    {
        return Ok(await proxyLifecycle.GetAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken));
    }

    [HttpPatch]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerProxySummary>> Update(
        Guid serverId,
        UpdateServerProxySettingsRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await proxyLifecycle.UpdateAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            request, cancellationToken));
    }

    [HttpPost("start")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerProxySummary>> Start(Guid serverId, CancellationToken cancellationToken)
    {
        return Ok(await proxyLifecycle.StartAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken));
    }

    [HttpPost("stop")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerProxySummary>> Stop(Guid serverId, CancellationToken cancellationToken)
    {
        return Ok(await proxyLifecycle.StopAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken));
    }

    [HttpPost("restart")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerProxySummary>> Restart(Guid serverId, CancellationToken cancellationToken)
    {
        return Ok(await proxyLifecycle.RestartAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken));
    }
}
