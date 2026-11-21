using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Resources;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers")]
public sealed class ServersController : ControllerBase
{
    private readonly ServerManagementService _management;
    private readonly ResourceManagementService _resources;

    public ServersController(ResourceManagementService resources, ServerManagementService management)
    {
        _resources = resources;
        _management = management;
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

    [HttpGet("{serverId:guid}")]
    public ActionResult<ServerDetails> Get(Guid serverId)
    {
        return Guard(() => Ok(_management.Get(User.GetUserId(), User.GetTeamId(), new ServerId(serverId))));
    }

    [HttpPatch("{serverId:guid}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> Update(Guid serverId, PatchServerRequest request, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await _management.UpdateAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), request, cancellationToken)));
    }

    [HttpDelete("{serverId:guid}")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> Delete(Guid serverId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await _management.DeleteAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
                cancellationToken);
            return NoContent();
        });
    }

    /// <summary>Queues a real validation (POST only, like upstream). Docker is installed only with <c>installDocker: true</c>.</summary>
    [HttpPost("{serverId:guid}/validate")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> Validate(Guid serverId, [FromBody] ValidateServerRequest? request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Accepted(await _management.RequestValidationAsync(User.GetUserId(),
            User.GetTeamId(), new ServerId(serverId), request ?? new ValidateServerRequest(), cancellationToken)));
    }

    [HttpPost("{serverId:guid}/host-key/trust")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> TrustHostKey(Guid serverId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await _management.TrustPendingHostKeyAsync(User.GetUserId(),
            User.GetTeamId(), new ServerId(serverId), cancellationToken)));
    }

    [HttpPost("{serverId:guid}/host-key/reset")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public Task<ActionResult> ResetHostKey(Guid serverId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await _management.ResetHostKeyAsync(User.GetUserId(), User.GetTeamId(),
            new ServerId(serverId), cancellationToken)));
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

    private ActionResult Guard(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (ApiErrors.TryMap(this, exception, out ActionResult? result))
        {
            return result!;
        }
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (ApiErrors.TryMap(this, exception, out ActionResult? result))
        {
            return result!;
        }
    }
}
