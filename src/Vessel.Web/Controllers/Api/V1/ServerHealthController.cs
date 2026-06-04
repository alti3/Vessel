using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Monitoring;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize]
[Route("api/v1/server-health")]
public sealed class ServerHealthController(
    IServerHealthQuery healthQuery,
    ServerHealthPollingService pollingService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = VesselPermissions.ServersRead)]
    public ActionResult<IReadOnlyList<ServerHealthSnapshotModel>> Latest()
    {
        return Ok(healthQuery.Latest(User.GetTeamId()));
    }

    [HttpPost("{serverId:guid}/poll")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerHealthPollingResult>> Poll(Guid serverId, CancellationToken cancellationToken)
    {
        return Accepted(await pollingService.PollAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new ServerId(serverId),
            cancellationToken));
    }
}
