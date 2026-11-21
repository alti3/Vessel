using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Proxy.Domains;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/services/{serviceId:guid}/domains")]
public sealed class ServiceDomainsController(ServiceDomainService domains) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ServiceDomainSummary>> List(Guid serviceId)
    {
        return Ok(domains.List(User.GetUserId(), User.GetTeamId(), new ServiceResourceId(serviceId)));
    }

    [HttpPut]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<ServiceDomainSummary>> Configure(
        Guid serviceId,
        ConfigureServiceDomainRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await domains.ConfigureAsync(User.GetUserId(), User.GetTeamId(), new ServiceResourceId(serviceId),
            request, cancellationToken));
    }

    [HttpDelete]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<IActionResult> Remove(
        Guid serviceId,
        [FromQuery] string address,
        CancellationToken cancellationToken)
    {
        await domains.RemoveAsync(User.GetUserId(), User.GetTeamId(), new ServiceResourceId(serviceId), address,
            cancellationToken);
        return NoContent();
    }
}
