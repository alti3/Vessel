using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Dashboard;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.SettingsManage)]
[Route("api/v1/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ISettingsCatalogQuery _settings;

    public SettingsController(ISettingsCatalogQuery settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<SettingListItem>> List()
    {
        return Ok(_settings.List(User.GetTeamId()));
    }
}
