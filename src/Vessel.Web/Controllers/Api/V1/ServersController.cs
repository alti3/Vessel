using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Dashboard;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ServersRead)]
[Route("api/v1/servers")]
public sealed class ServersController : ControllerBase
{
    private readonly IServerCatalogQuery _servers;

    public ServersController(IServerCatalogQuery servers)
    {
        _servers = servers;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<ServerListItem>> List()
    {
        return Ok(_servers.List(User.GetTeamId()));
    }
}
