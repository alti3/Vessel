using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Shared.Errors;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Notification targets and the in-app notification center. Reads need <c>notifications.read</c>; changing
///     targets or sending a test needs <c>notifications.write</c> (owners/admins). The Application services enforce
///     the same permissions against the caller's team role, so a token scope alone is never sufficient.
///     Notification events are raised only by Vessel workflows; there is no public endpoint for arbitrary events.
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.NotificationsRead)]
[Route("api/v1/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly NotificationTargetService _targets;
    private readonly NotificationCenterService _center;

    public NotificationsController(
        NotificationTargetService targets,
        NotificationCenterService center)
    {
        _targets = targets;
        _center = center;
    }

    [HttpGet("targets")]
    public ActionResult<IReadOnlyList<NotificationTargetListItem>> ListTargets()
    {
        return Guard(() => Ok(_targets.ListTargets(User.GetTeamId(), User.GetUserId())));
    }

    /// <summary>The notification event catalog: keys, defaults, always-deliver events, and throttling (NTF-05).</summary>
    [HttpGet("event-types")]
    public ActionResult<IReadOnlyList<NotificationEventCatalogItem>> ListEvents()
    {
        return Guard(() => Ok(_targets.ListEventCatalog(User.GetTeamId(), User.GetUserId())));
    }

    [HttpPut("targets")]
    [Authorize(Policy = VesselPermissions.NotificationsWrite)]
    public async Task<ActionResult<NotificationTargetListItem>> UpsertTarget(
        UpsertNotificationTargetRequest request,
        CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
            Ok(await _targets.UpsertTargetAsync(User.GetTeamId(), User.GetUserId(), request, cancellationToken)));
    }

    [HttpPost("targets/{targetId:guid}/test")]
    [Authorize(Policy = VesselPermissions.NotificationsWrite)]
    public async Task<ActionResult<object>> SendTest(Guid targetId, CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            var eventId = await _targets.SendTestAsync(User.GetTeamId(), User.GetUserId(),
                new NotificationTargetId(targetId), cancellationToken);
            return Accepted(new { id = eventId });
        });
    }

    [HttpGet("in-app")]
    public ActionResult<IReadOnlyList<InAppNotificationListItem>> ListInApp([FromQuery] bool includeArchived = false)
    {
        return Guard(() => Ok(_center.ListInApp(User.GetTeamId(), User.GetUserId(), includeArchived)));
    }

    [HttpPost("in-app/{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            await _center.MarkReadAsync(User.GetTeamId(), User.GetUserId(), new InAppNotificationId(notificationId),
                cancellationToken);
            return NoContent();
        });
    }

    [HttpPost("in-app/{notificationId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid notificationId, CancellationToken cancellationToken)
    {
        return await GuardAsync(async () =>
        {
            await _center.ArchiveAsync(User.GetTeamId(), User.GetUserId(), new InAppNotificationId(notificationId),
                cancellationToken);
            return NoContent();
        });
    }

    [Obsolete("Use GET /api/v1/notifications/targets.")]
    [HttpGet]
    public ActionResult<IReadOnlyList<NotificationTargetListItem>> List()
    {
        return ListTargets();
    }

    private ActionResult Guard(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (DomainException exception)
        {
            // Domain/validation messages are written to be safe for display; they never echo secrets.
            return BadRequest(new ApiErrorResponse(new ApiError("invalid_notification_target", exception.Message,
                new Dictionary<string, object?>())));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }
}
