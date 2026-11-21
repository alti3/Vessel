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

    /// <summary>
    ///     One template with its metadata (source, documentation, tags, port, routed endpoints and import warnings such
    ///     as major-version-only image tags). Imported Coolify templates use keys prefixed <c>coolify-</c>.
    /// </summary>
    [HttpGet("{key}")]
    public ActionResult<ServiceTemplateSummary> Get(string key)
    {
        try
        {
            return Ok(managedServices.GetTemplate(key));
        }
        catch (Exception exception) when (ApiErrors.TryMap(this, exception, out ActionResult? result))
        {
            return result!;
        }
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
