using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Dashboard;
using Vessel.Application.Deployments;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize]
[Route("api/v1/deployments")]
public sealed class DeploymentsController : ControllerBase
{
    private readonly IDeploymentCatalogQuery _deployments;
    private readonly StartDeploymentService _starter;
    private readonly DeploymentQueryService _details;

    public DeploymentsController(
        IDeploymentCatalogQuery deployments,
        StartDeploymentService starter,
        DeploymentQueryService details)
    {
        _deployments = deployments;
        _starter = starter;
        _details = details;
    }

    [HttpGet]
    [Authorize(Policy = VesselPermissions.DeploymentsReadLogs)]
    public ActionResult<IReadOnlyList<DeploymentListItem>> List()
    {
        return Ok(_deployments.List(User.GetTeamId()));
    }

    [HttpGet("{deploymentId:guid}")]
    [Authorize(Policy = VesselPermissions.DeploymentsReadLogs)]
    public ActionResult<DeploymentDetails> Get(Guid deploymentId)
    {
        return Ok(_details.Get(User.GetUserId(), User.GetTeamId(), new DeploymentId(deploymentId)));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.DeploymentsStart)]
    public async Task<ActionResult<StartDeploymentResult>> Start(
        StartDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        StartDeploymentResult result = await _starter.StartAsync(
            User.GetUserId(),
            User.GetTeamId(),
            request,
            cancellationToken);
        return AcceptedAtAction(nameof(Get), new { deploymentId = result.DeploymentId }, result);
    }

    [HttpPost("{deploymentId:guid}/cancel")]
    [Authorize(Policy = VesselPermissions.DeploymentsCancel)]
    public async Task<IActionResult> Cancel(Guid deploymentId, CancellationToken cancellationToken)
    {
        await _starter.CancelAsync(User.GetUserId(), User.GetTeamId(), new DeploymentId(deploymentId), cancellationToken);
        return Accepted();
    }
}
