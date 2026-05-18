using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Dashboard;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize]
[Route("api/v1/dashboard")]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardOverviewQuery _dashboard;

    public DashboardController(IDashboardOverviewQuery dashboard)
    {
        _dashboard = dashboard;
    }

    [HttpGet]
    public ActionResult<DashboardOverview> Get()
    {
        return Ok(_dashboard.GetOverview(User.GetTeamId()));
    }
}
