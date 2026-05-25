using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Resources;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/environment-variables")]
public sealed class EnvironmentVariablesController(ResourceManagementService resources) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<EnvironmentVariableSummary>> List()
    {
        return Ok(resources.ListEnvironmentVariables(User.GetUserId(), User.GetTeamId()));
    }

    [HttpPost]
    [Authorize(Policy = VesselPermissions.SecretsWrite)]
    public async Task<ActionResult<EnvironmentVariableSummary>> Create(
        CreateEnvironmentVariableRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await resources.CreateEnvironmentVariableAsync(User.GetUserId(), User.GetTeamId(), request,
            cancellationToken));
    }

    [HttpGet("{variableId:guid}/reveal")]
    [Authorize(Policy = VesselPermissions.SecretsRead)]
    public async Task<ActionResult<string>> Reveal(Guid variableId, CancellationToken cancellationToken)
    {
        return Ok(await resources.RevealEnvironmentVariableAsync(User.GetUserId(), User.GetTeamId(),
            new EnvironmentVariableId(variableId), cancellationToken));
    }
}
