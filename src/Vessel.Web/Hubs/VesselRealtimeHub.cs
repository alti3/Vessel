using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Vessel.Web.Hubs;

[Authorize]
public sealed class VesselRealtimeHub : Hub;
