using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Resources.RuntimeLogs;
using Vessel.Application.Resources.Status;
using Vessel.Domain.Monitoring;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Runtime logs (SRV-05) and runtime status / restart limits (SRV-04) of applications, databases and services:
///     <c>/api/v1/{applications|databases|services}/{id}/…</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/{kind:regex(^(applications|databases|services)$)}/{resourceId:guid}")]
public sealed class ResourceRuntimeController(
    RuntimeLogQueryService logs,
    ResourceRuntimeStatusService status) : ControllerBase
{
    /// <summary>Merged runtime logs. <c>lines</c>: a number, or <c>all</c>/<c>-1</c>; <c>since</c>: ISO time.</summary>
    [HttpGet("logs")]
    [Authorize(Policy = VesselPermissions.ResourcesReadLogs)]
    public async Task<ActionResult<RuntimeLogResultDto>> Logs(string kind, Guid resourceId,
        [FromQuery] string? lines, [FromQuery] DateTimeOffset? since, [FromQuery] bool timestamps = true,
        CancellationToken cancellationToken = default)
    {
        return Ok(await logs.ReadAsync(User.GetUserId(), User.GetTeamId(), Kind(kind), resourceId,
            new RuntimeLogQuery(ParseLines(lines), since, timestamps), cancellationToken));
    }

    /// <summary>Redacted plain-text log export.</summary>
    [HttpGet("logs/export")]
    [Authorize(Policy = VesselPermissions.ResourcesReadLogs)]
    public async Task<IActionResult> Export(string kind, Guid resourceId, [FromQuery] string? lines,
        [FromQuery] DateTimeOffset? since, CancellationToken cancellationToken = default)
    {
        var text = await logs.ExportAsync(User.GetUserId(), User.GetTeamId(), Kind(kind), resourceId,
            new RuntimeLogQuery(ParseLines(lines) ?? RuntimeLogQueryService.MaxLines, since), cancellationToken);
        return File(Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", $"{kind}-{resourceId:N}-logs.txt");
    }

    [HttpGet("runtime-status")]
    public ActionResult<ResourceRuntimeStatusDto> Status(string kind, Guid resourceId)
    {
        return Ok(status.Get(User.GetUserId(), User.GetTeamId(), Kind(kind), resourceId));
    }

    /// <summary>Opt-in restart limit (0 disables it).</summary>
    [HttpPut("restart-limit")]
    [Authorize(Policy = VesselPermissions.ResourcesLifecycle)]
    public async Task<ActionResult<ResourceRuntimeStatusDto>> ConfigureRestartLimit(string kind, Guid resourceId,
        UpdateRestartLimitRequest request, CancellationToken cancellationToken)
    {
        return Ok(await status.ConfigureRestartLimitAsync(User.GetUserId(), User.GetTeamId(), Kind(kind),
            resourceId, request, cancellationToken));
    }

    [HttpPost("restart-limit/reset")]
    [Authorize(Policy = VesselPermissions.ResourcesLifecycle)]
    public async Task<ActionResult<ResourceRuntimeStatusDto>> ResetRestartLimit(string kind, Guid resourceId,
        CancellationToken cancellationToken)
    {
        return Ok(await status.ResetRestartLimitAsync(User.GetUserId(), User.GetTeamId(), Kind(kind), resourceId,
            cancellationToken));
    }

    public static ResourceRuntimeKind Kind(string kind)
    {
        return kind switch
        {
            "databases" => ResourceRuntimeKind.Database,
            "services" => ResourceRuntimeKind.Service,
            _ => ResourceRuntimeKind.Application
        };
    }

    /// <summary><c>all</c> or a negative number reads the whole log (still capped); missing uses the default.</summary>
    public static int? ParseLines(string? lines)
    {
        if (string.IsNullOrWhiteSpace(lines)) return RuntimeLogQueryService.DefaultLines;
        if (string.Equals(lines, "all", StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(lines, out var value))
            throw new ArgumentException("lines must be a number or 'all'.", nameof(lines));
        return value < 0 ? null : value;
    }
}
