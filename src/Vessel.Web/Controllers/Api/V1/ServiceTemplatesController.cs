using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.ManagedServices;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/service-templates")]
public sealed class ServiceTemplatesController(ManagedDatabaseService managedServices) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ServiceTemplateSummary>> List()
    {
        return Ok(managedServices.ListTemplates());
    }

    [HttpPost("resources")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<ServiceResourceSummary>> Create(
        CreateServiceFromTemplateRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await managedServices.CreateServiceFromTemplateAsync(
            User.GetUserId(),
            User.GetTeamId(),
            request,
            cancellationToken));
    }
}
