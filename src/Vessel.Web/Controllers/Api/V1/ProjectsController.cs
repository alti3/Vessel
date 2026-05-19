using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Resources;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly ResourceManagementService _resources;

    public ProjectsController(ResourceManagementService resources)
    {
        _resources = resources;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<ProjectSummary>> List()
    {
        return Ok(_resources.ListProjects(User.GetUserId(), User.GetTeamId()));
    }

    [HttpGet("{projectId:guid}")]
    public ActionResult<ProjectDetails> Get(Guid projectId)
    {
        return Ok(_resources.GetProject(User.GetUserId(), User.GetTeamId(), new ProjectId(projectId)));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<ProjectDetails>> Create(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        ProjectDetails project = await _resources.CreateProjectAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { projectId = project.Id }, project);
    }

    [HttpPut("{projectId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<ActionResult<ProjectDetails>> Update(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _resources.UpdateProjectAsync(User.GetUserId(), User.GetTeamId(), new ProjectId(projectId), request, cancellationToken));
    }

    [HttpDelete("{projectId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public async Task<IActionResult> Archive(Guid projectId, CancellationToken cancellationToken)
    {
        await _resources.ArchiveProjectAsync(User.GetUserId(), User.GetTeamId(), new ProjectId(projectId), cancellationToken);
        return NoContent();
    }
}
