using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Resources;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/databases")]
public sealed class DatabasesController : ControllerBase
{
    private readonly ResourceManagementService _resources;

    public DatabasesController(ResourceManagementService resources)
    {
        _resources = resources;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<DatabaseSummary>> List()
    {
        return Ok(_resources.ListDatabases(User.GetUserId(), User.GetTeamId()));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<DatabaseSummary>> Create(CreateDatabaseRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _resources.CreateDatabaseAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken));
    }
}
