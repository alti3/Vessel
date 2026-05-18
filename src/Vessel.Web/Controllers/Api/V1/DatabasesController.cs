using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Dashboard;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/databases")]
public sealed class DatabasesController : ControllerBase
{
    private readonly IDatabaseCatalogQuery _databases;

    public DatabasesController(IDatabaseCatalogQuery databases)
    {
        _databases = databases;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<DatabaseListItem>> List()
    {
        return Ok(_databases.List(User.GetTeamId()));
    }
}
