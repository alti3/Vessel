using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Proxy;
using Vessel.Web.Security;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ApplicationsRead)]
[Route("api/v1/applications/{applicationId:guid}/domains")]
public sealed class ApplicationDomainsController(DomainRoutingService domains, CertificateManagementService certificates)
    : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<DomainRouteSummary>> List(Guid applicationId)
    {
        return Ok(domains.List(User.GetUserId(), User.GetTeamId(), new AppId(applicationId)));
    }

    [HttpPut("{host}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<ActionResult<DomainRouteSummary>> Configure(
        Guid applicationId,
        string host,
        ConfigureDomainRouteRequest request,
        CancellationToken cancellationToken)
    {
        var effective = request with { Host = host };
        return Ok(await domains.ConfigureAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId),
            effective, cancellationToken));
    }

    [HttpDelete("{host}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<IActionResult> Remove(Guid applicationId, string host, CancellationToken cancellationToken)
    {
        await domains.RemoveAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId), host, cancellationToken);
        return NoContent();
    }

    [HttpPost("{host}/certificates/issue")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<ActionResult<CertificateSummary>> QueueCertificate(
        Guid applicationId,
        string host,
        CancellationToken cancellationToken)
    {
        return Ok(await certificates.QueueIssuanceAsync(User.GetUserId(), User.GetTeamId(),
            new AppId(applicationId), host, cancellationToken));
    }
}
