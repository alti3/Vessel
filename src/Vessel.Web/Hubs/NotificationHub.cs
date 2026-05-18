using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;

namespace Vessel.Web.Hubs;

[Authorize]
public sealed class NotificationHub : AuthorizedResourceHub
{
    public NotificationHub(VesselAuthorizationService authorizationService)
        : base(authorizationService)
    {
    }

    public override async Task OnConnectedAsync()
    {
        await JoinTenantGroupAsync();
        await JoinUserGroupAsync();
        await base.OnConnectedAsync();
    }
}
