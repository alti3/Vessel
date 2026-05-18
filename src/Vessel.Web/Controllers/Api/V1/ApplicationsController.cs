using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Dashboard;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ApplicationsRead)]
[Route("api/v1/applications")]
public sealed class ApplicationsController : ControllerBase
{
    private readonly IApplicationCatalogQuery _applications;

    public ApplicationsController(IApplicationCatalogQuery applications)
    {
        _applications = applications;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<ApplicationListItem>> List()
    {
        return Ok(_applications.List(User.GetTeamId()));
    }
}
