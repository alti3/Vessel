using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Dashboard;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.DeploymentsReadLogs)]
[Route("api/v1/deployments")]
public sealed class DeploymentsController : ControllerBase
{
    private readonly IDeploymentCatalogQuery _deployments;

    public DeploymentsController(IDeploymentCatalogQuery deployments)
    {
        _deployments = deployments;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<DeploymentListItem>> List()
    {
        return Ok(_deployments.List(User.GetTeamId()));
    }
}
