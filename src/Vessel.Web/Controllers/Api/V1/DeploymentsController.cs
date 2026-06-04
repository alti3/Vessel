using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    private readonly DeploymentQueryService _details;
    private readonly StartDeploymentService _starter;

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

    [HttpGet("{deploymentId:guid}/logs")]
    [Authorize(Policy = VesselPermissions.DeploymentsReadLogs)]
    public ActionResult<DeploymentLogPage> Logs(
        Guid deploymentId,
        [FromQuery] int? afterSequence,
        [FromQuery] string? search,
        [FromQuery] string? stream,
        [FromQuery] int pageSize = 200,
        [FromQuery] bool descending = false)
    {
        return Ok(_details.GetLogs(
            User.GetUserId(),
            User.GetTeamId(),
            new DeploymentId(deploymentId),
            new DeploymentLogQuery(afterSequence, search, stream, pageSize, descending)));
    }

    [HttpGet("{deploymentId:guid}/logs/export")]
    [Authorize(Policy = VesselPermissions.DeploymentsReadLogs)]
    public FileContentResult ExportLogs(
        Guid deploymentId,
        [FromQuery] int? afterSequence,
        [FromQuery] string? search,
        [FromQuery] string? stream,
        [FromQuery] int pageSize = 1000,
        [FromQuery] bool descending = false)
    {
        DeploymentLogPage page = _details.GetLogs(
            User.GetUserId(),
            User.GetTeamId(),
            new DeploymentId(deploymentId),
            new DeploymentLogQuery(afterSequence, search, stream, pageSize, descending));

        string text = string.Join(
            Environment.NewLine,
            page.Entries.Select(entry =>
                $"{entry.Sequence}\t{entry.CreatedAt:O}\t{entry.Stream}\t{entry.Message}"));

        return File(
            Encoding.UTF8.GetBytes(text),
            "text/plain; charset=utf-8",
            $"deployment-{deploymentId:D}-logs.txt");
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
    [EnableRateLimiting("api")]
    public async Task<IActionResult> Cancel(Guid deploymentId, CancellationToken cancellationToken)
    {
        await _starter.CancelAsync(User.GetUserId(), User.GetTeamId(), new DeploymentId(deploymentId),
            cancellationToken);
        return Accepted();
    }
}
