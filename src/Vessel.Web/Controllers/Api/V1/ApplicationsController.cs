using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Resources;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ApplicationsRead)]
[Route("api/v1/applications")]
public sealed class ApplicationsController : ControllerBase
{
    private readonly ResourceManagementService _resources;

    public ApplicationsController(ResourceManagementService resources)
    {
        _resources = resources;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<ApplicationSummary>> List()
    {
        return Ok(_resources.ListApplications(User.GetUserId(), User.GetTeamId()));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<ActionResult<ApplicationSummary>> Create(CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _resources.CreateApplicationAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken));
    }
}
