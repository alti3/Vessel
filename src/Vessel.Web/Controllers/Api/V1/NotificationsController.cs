using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Dashboard;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize]
[Route("api/v1/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly INotificationCatalogQuery _notifications;

    public NotificationsController(INotificationCatalogQuery notifications)
    {
        _notifications = notifications;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<NotificationTargetListItem>> List()
    {
        return Ok(_notifications.List(User.GetTeamId()));
    }
}
