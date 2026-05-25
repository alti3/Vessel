using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Resources;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers")]
public sealed class ServersController : ControllerBase
{
    private readonly ResourceManagementService _resources;

    public ServersController(ResourceManagementService resources)
    {
        _resources = resources;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<ServerSummary>> List()
    {
        return Ok(_resources.ListServers(User.GetUserId(), User.GetTeamId()));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerSummary>> Create(CreateServerRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _resources.CreateServerAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken));
    }

    [HttpPost("{serverId:guid}/connectivity-test")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerConnectivityResult>> Test(Guid serverId, CancellationToken cancellationToken)
    {
        return Ok(await _resources.TestServerConnectivityAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), cancellationToken));
    }

    [HttpGet("{serverId:guid}/snapshots")]
    public ActionResult<IReadOnlyList<ServerStatusSnapshotSummary>> Snapshots(Guid serverId)
    {
        return Ok(_resources.ListServerSnapshots(User.GetUserId(), User.GetTeamId(), new ServerId(serverId)));
    }
}
