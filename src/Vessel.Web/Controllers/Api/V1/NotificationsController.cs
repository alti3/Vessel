using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Notifications;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize]
[Route("api/v1/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly NotificationTargetService _targets;
    private readonly NotificationCenterService _center;
    private readonly NotificationEventService _events;

    public NotificationsController(
        NotificationTargetService targets,
        NotificationCenterService center,
        NotificationEventService events)
    {
        _targets = targets;
        _center = center;
        _events = events;
    }

    [HttpGet("targets")]
    public ActionResult<IReadOnlyList<NotificationTargetListItem>> ListTargets()
    {
        return Ok(_targets.ListTargets(User.GetTeamId()));
    }

    [HttpPut("targets")]
    public async Task<ActionResult<NotificationTargetListItem>> UpsertTarget(
        UpsertNotificationTargetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _targets.UpsertTargetAsync(User.GetTeamId(), User.GetUserId(), request, cancellationToken);
        return Ok(result);
    }

    [HttpGet("in-app")]
    public ActionResult<IReadOnlyList<InAppNotificationListItem>> ListInApp([FromQuery] bool includeArchived = false)
    {
        return Ok(_center.ListInApp(User.GetTeamId(), User.GetUserId(), includeArchived));
    }

    [HttpPost("in-app/{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        await _center.MarkReadAsync(User.GetTeamId(), User.GetUserId(), new InAppNotificationId(notificationId),
            cancellationToken);
        return NoContent();
    }

    [HttpPost("in-app/{notificationId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid notificationId, CancellationToken cancellationToken)
    {
        await _center.ArchiveAsync(User.GetTeamId(), User.GetUserId(), new InAppNotificationId(notificationId),
            cancellationToken);
        return NoContent();
    }

    [HttpPost("events")]
    public async Task<ActionResult<object>> Raise(RaiseNotificationRequest request, CancellationToken cancellationToken)
    {
        var eventId = await _events.RaiseAsync(User.GetTeamId(), request, cancellationToken);
        return Accepted(new { id = eventId });
    }

    [Obsolete("Use GET /api/v1/notifications/targets.")]
    [HttpGet]
    public ActionResult<IReadOnlyList<NotificationTargetListItem>> List()
    {
        return Ok(_targets.ListTargets(User.GetTeamId()));
    }
}
