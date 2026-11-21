using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Servers.DockerCleanup;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>Docker cleanup policy, runs and history, and the server Images page (SRV-07).</summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers/{serverId:guid}")]
public sealed class ServerDockerCleanupController(DockerCleanupService cleanup) : ControllerBase
{
    [HttpGet("docker-cleanup")]
    public ActionResult<DockerCleanupPolicyDto> GetPolicy(Guid serverId)
    {
        return Ok(cleanup.GetPolicy(User.GetUserId(), User.GetTeamId(), new ServerId(serverId)));
    }

    [HttpPatch("docker-cleanup")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<DockerCleanupPolicyDto>> UpdatePolicy(Guid serverId,
        UpdateDockerCleanupPolicyRequest request, CancellationToken cancellationToken)
    {
        return Ok(await cleanup.UpdatePolicyAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            request, cancellationToken));
    }

    [HttpPost("docker-cleanup/run")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<IActionResult> Run(Guid serverId, CancellationToken cancellationToken)
    {
        var jobId = await cleanup.RequestManualRunAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken);
        return Accepted(new { jobId });
    }

    [HttpGet("docker-cleanup/executions")]
    public ActionResult<IReadOnlyList<DockerCleanupExecutionDto>> Executions(Guid serverId,
        [FromQuery] int take = 50)
    {
        return Ok(cleanup.ListExecutions(User.GetUserId(), User.GetTeamId(), new ServerId(serverId), take));
    }

    [HttpGet("images")]
    public async Task<ActionResult<ServerImagesPageDto>> Images(Guid serverId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        return Ok(await cleanup.ListImagesAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId), page,
            pageSize, cancellationToken));
    }

    /// <summary>Deletes one unused image; the body must repeat the reference (typed confirmation).</summary>
    [HttpPost("images/delete")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<IActionResult> DeleteImage(Guid serverId, DeleteServerImageRequest request,
        CancellationToken cancellationToken)
    {
        await cleanup.DeleteImageAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId), request,
            cancellationToken);
        return NoContent();
    }
}
