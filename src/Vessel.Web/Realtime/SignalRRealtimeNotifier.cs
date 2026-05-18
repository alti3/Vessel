using Microsoft.AspNetCore.SignalR;
using Vessel.Application.Realtime;
using Vessel.Web.Hubs;

namespace Vessel.Web.Realtime;

public sealed class SignalRRealtimeNotifier(IHubContext<VesselRealtimeHub> hubContext) : IRealtimeNotifier
{
    public Task PublishAsync(RealtimeGroup group, RealtimeMessage message, CancellationToken cancellationToken = default) =>
        hubContext.Clients.Group(group.ToString()).SendAsync(message.Type, message.Payload, cancellationToken);
}
