using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments;
using Vessel.Application.Deployments.Previews;
using Vessel.Application.Deployments.Rollback;
using Vessel.Application.Deployments.Settings;
using Vessel.Application.Webhooks;
using Vessel.Web.Security;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Per-application deployment endpoints: deployment settings (previews, public previews, auto-cancel, log
///     redaction, image retention), rollback targets and rollback, and preview lifecycle. Permission split follows
///     Coolify: reading needs read, redeploying or stopping a preview and rolling back need deploy, changing settings
///     or closing a preview needs write.
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ApplicationsRead)]
[Route("api/v1/applications/{applicationId:guid}")]
public sealed class ApplicationDeploymentsController(
    DeploymentSettingsService settings,
    RollbackService rollbacks,
    PreviewLifecycleService previews) : ControllerBase
{
    [HttpGet("deployment-settings")]
    public ActionResult<ApplicationDeploymentSettingsDto> GetSettings(Guid applicationId)
    {
        return Ok(settings.Get(User.GetUserId(), User.GetTeamId(), new AppId(applicationId)));
    }

    [HttpPatch("deployment-settings")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<ActionResult<ApplicationDeploymentSettingsDto>> UpdateSettings(
        Guid applicationId,
        UpdateApplicationDeploymentSettingsRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await settings.UpdateAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId), request,
            cancellationToken));
    }

    [HttpGet("rollback-targets")]
    public async Task<ActionResult<IReadOnlyList<RollbackTarget>>> RollbackTargets(Guid applicationId,
        CancellationToken cancellationToken)
    {
        return Ok(await rollbacks.ListTargetsAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId),
            cancellationToken));
    }

    [HttpPost("rollback")]
    [Authorize(Policy = VesselPermissions.DeploymentsStart)]
    [EnableRateLimiting("api")]
    public async Task<ActionResult<StartDeploymentResult>> Rollback(Guid applicationId, RollbackRequest request,
        CancellationToken cancellationToken)
    {
        StartDeploymentResult result = await rollbacks.RollbackAsync(User.GetUserId(), User.GetTeamId(),
            new AppId(applicationId), request, cancellationToken);
        return Accepted(result);
    }

    [HttpGet("previews")]
    public ActionResult<IReadOnlyList<ApplicationPreviewSummary>> Previews(Guid applicationId)
    {
        return Ok(previews.List(User.GetUserId(), User.GetTeamId(), new AppId(applicationId)));
    }

    [HttpGet("previews/{pullRequestNumber:int}")]
    public ActionResult<ApplicationPreviewSummary> Preview(Guid applicationId, int pullRequestNumber)
    {
        return Ok(previews.Get(User.GetUserId(), User.GetTeamId(), new AppId(applicationId), pullRequestNumber));
    }

    [HttpPost("previews/{pullRequestNumber:int}/deploy")]
    [Authorize(Policy = VesselPermissions.DeploymentsStart)]
    [EnableRateLimiting("api")]
    public async Task<ActionResult<StartDeploymentResult>> RedeployPreview(Guid applicationId, int pullRequestNumber,
        [FromQuery] bool forceRebuild, CancellationToken cancellationToken)
    {
        return Accepted(await previews.RedeployAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId),
            pullRequestNumber, forceRebuild, cancellationToken));
    }

    [HttpPost("previews/{pullRequestNumber:int}/stop")]
    [Authorize(Policy = VesselPermissions.DeploymentsStart)]
    public async Task<ActionResult<ApplicationPreviewSummary>> StopPreview(Guid applicationId, int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        return Ok(await previews.StopAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId),
            pullRequestNumber, cancellationToken));
    }

    [HttpDelete("previews/{pullRequestNumber:int}")]
    [Authorize(Policy = VesselPermissions.ApplicationsWrite)]
    public async Task<ActionResult<ApplicationPreviewSummary>> ClosePreview(Guid applicationId, int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        return Ok(await previews.CloseAsync(User.GetUserId(), User.GetTeamId(), new AppId(applicationId),
            pullRequestNumber, cancellationToken));
    }
}
