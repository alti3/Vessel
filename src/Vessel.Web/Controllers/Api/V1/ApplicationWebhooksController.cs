using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Webhooks;
using Vessel.Domain;
using Vessel.Web.Security;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ApplicationsRead)]
[Route("api/v1/applications/{applicationId:guid}")]
public sealed class ApplicationWebhooksController(ApplicationWebhookConfigurationService webhooks) : ControllerBase
{
    [HttpGet("webhooks")]
    public ActionResult<IReadOnlyList<ApplicationWebhookConfigurationSummary>> List(Guid applicationId)
    {
        return Ok(webhooks.List(User.GetUserId(), User.GetTeamId(), new AppId(applicationId)));
    }

    [HttpPut("webhooks")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<ActionResult<ApplicationWebhookConfigurationSummary>> Configure(
        Guid applicationId,
        ConfigureApplicationWebhookRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await webhooks.ConfigureAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new AppId(applicationId),
            request,
            cancellationToken));
    }

    [HttpGet("git/refs")]
    public async Task<ActionResult<IReadOnlyList<GitRepositoryRefSummary>>> Refs(
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        return Ok(await webhooks.ListRefsAsync(
            User.GetUserId(),
            User.GetTeamId(),
            new AppId(applicationId),
            cancellationToken));
    }
}
