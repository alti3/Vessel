using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers/{serverId:guid}/proxy-configurations")]
public sealed class ProxyConfigurationsController(ProxyConfigurationService proxyConfigurations) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ProxyConfigurationSummary>> List(Guid serverId)
    {
        return Ok(proxyConfigurations.ListVersions(User.GetUserId(), User.GetTeamId(), new ServerId(serverId)));
    }

    [HttpPost("apply")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ProxyConfigurationSummary>> Apply(Guid serverId, CancellationToken cancellationToken)
    {
        return Ok(await proxyConfigurations.GenerateValidateAndApplyAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), cancellationToken));
    }

    [HttpPost("{versionId:guid}/rollback")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ProxyConfigurationSummary>> Rollback(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        return Ok(await proxyConfigurations.RollbackAsync(User.GetUserId(), User.GetTeamId(),
            new ProxyConfigurationVersionId(versionId), cancellationToken));
    }
}
