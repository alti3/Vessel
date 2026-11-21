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
public sealed class ApplicationDomainsController(
    DomainRoutingService domains,
    CertificateManagementService certificates)
    : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<DomainRouteSummary>> List(Guid applicationId)
    {
        return Ok(domains.List(User.GetUserId(), User.GetTeamId(), new AppId(applicationId)));
    }

    /// <summary>Configures a route given as a URL in the body (supports paths, wildcards and IP hosts).</summary>
    [HttpPut]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<ActionResult<DomainRouteSummary>> ConfigureByUrl(
        Guid applicationId,
        ConfigureDomainRouteRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await domains.ConfigureAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId),
            request, cancellationToken));
    }

    [HttpPut("{host}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<ActionResult<DomainRouteSummary>> Configure(
        Guid applicationId,
        string host,
        ConfigureDomainRouteRequest request,
        CancellationToken cancellationToken)
    {
        ConfigureDomainRouteRequest effective = request with { Host = host };
        return Ok(await domains.ConfigureAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId),
            effective, cancellationToken));
    }

    /// <summary>Removes the route identified by a URL or host (with optional path) in the query string.</summary>
    [HttpDelete]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<IActionResult> RemoveByUrl(
        Guid applicationId,
        [FromQuery] string address,
        CancellationToken cancellationToken)
    {
        await domains.RemoveAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId), address,
            cancellationToken);
        return NoContent();
    }

    [HttpDelete("{host}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<IActionResult> Remove(Guid applicationId, string host, CancellationToken cancellationToken)
    {
        await domains.RemoveAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId), host,
            cancellationToken);
        return NoContent();
    }

    [HttpGet("certificates")]
    public ActionResult<IReadOnlyList<CertificateSummary>> Certificates(Guid applicationId)
    {
        return Ok(certificates.List(User.GetUserId(), User.GetTeamId(), new AppId(applicationId)));
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
