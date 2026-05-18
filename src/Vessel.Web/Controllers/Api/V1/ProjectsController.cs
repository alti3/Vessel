using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Dashboard;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly IProjectCatalogQuery _projects;

    public ProjectsController(IProjectCatalogQuery projects)
    {
        _projects = projects;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<ProjectListItem>> List()
    {
        return Ok(_projects.List(User.GetTeamId()));
    }
}
