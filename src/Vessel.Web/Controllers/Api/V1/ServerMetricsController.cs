using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>Host and container metrics (SRV-03) and per-server monitoring settings (SRV-08).</summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1")]
public sealed class ServerMetricsController(
    ServerMetricsQueryService metrics,
    ServerMetricsCollectionService collection,
    ServerMonitoringSettingsService settings) : ControllerBase
{
    /// <summary>Metric series of one server. <c>resolution</c>: auto (default), raw (24 h) or hourly (30 days).</summary>
    [HttpGet("servers/{serverId:guid}/metrics")]
    public ActionResult<ServerMetricsDto> Get(Guid serverId, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, [FromQuery] string? resolution)
    {
        return Ok(metrics.GetServerMetrics(User.GetUserId(), User.GetTeamId(), new ServerId(serverId), from, to,
            resolution));
    }

    [HttpPost("servers/{serverId:guid}/metrics/collect")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerMetricsCollectionResult>> Collect(Guid serverId,
        CancellationToken cancellationToken)
    {
        return Ok(await collection.CollectAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId),
            cancellationToken));
    }

    /// <summary>Fleet metrics: every readable server of the active team with KPIs and the top 20 containers.</summary>
    [HttpGet("metrics/fleet")]
    public ActionResult<FleetMetricsDto> Fleet()
    {
        return Ok(metrics.GetFleet(User.GetUserId(), User.GetTeamId()));
    }

    [HttpGet("servers/{serverId:guid}/monitoring")]
    public ActionResult<ServerMonitoringSettingsDto> GetSettings(Guid serverId)
    {
        return Ok(settings.Get(User.GetUserId(), User.GetTeamId(), new ServerId(serverId)));
    }

    [HttpPatch("servers/{serverId:guid}/monitoring")]
    [Authorize(Policy = VesselPermissions.ServersWrite)]
    public async Task<ActionResult<ServerMonitoringSettingsDto>> UpdateSettings(Guid serverId,
        UpdateServerMonitoringSettingsRequest request, CancellationToken cancellationToken)
    {
        return Ok(await settings.UpdateAsync(User.GetUserId(), User.GetTeamId(), new ServerId(serverId), request,
            cancellationToken));
    }
}
