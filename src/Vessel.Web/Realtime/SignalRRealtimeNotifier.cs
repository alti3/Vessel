using Microsoft.AspNetCore.SignalR;
using Vessel.Application.Realtime;
using Vessel.Web.Hubs;

namespace Vessel.Web.Realtime;

public sealed class SignalRRealtimeNotifier(
    IHubContext<VesselRealtimeHub> hubContext,
    IHubContext<DeploymentLogHub> deploymentLogHubContext,
    IHubContext<TerminalHub> terminalHubContext,
    IHubContext<ServerStatusHub> serverStatusHubContext,
    IHubContext<NotificationHub> notificationHubContext) : IRealtimeNotifier
{
    public Task PublishAsync(RealtimeGroup group, RealtimeMessage message,
        CancellationToken cancellationToken = default)
    {
        IClientProxy clients = group.Kind switch
        {
            RealtimeGroupKind.Deployment => deploymentLogHubContext.Clients.Group(group.ToString()),
            RealtimeGroupKind.Terminal => terminalHubContext.Clients.Group(group.ToString()),
            RealtimeGroupKind.Server => serverStatusHubContext.Clients.Group(group.ToString()),
            RealtimeGroupKind.Team or RealtimeGroupKind.User => notificationHubContext.Clients.Group(group.ToString()),
            _ => hubContext.Clients.Group(group.ToString())
        };

        return clients.SendAsync(message.Type, message.Payload, cancellationToken);
    }
}
