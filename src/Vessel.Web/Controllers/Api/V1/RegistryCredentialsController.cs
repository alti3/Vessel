using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.Resources;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

[ApiController]
[Authorize(Policy = VesselPermissions.SecretsWrite)]
[Route("api/v1/registry-credentials")]
public sealed class RegistryCredentialsController(ResourceManagementService resources) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<RegistryCredentialSummary>> Create(
        CreateRegistryCredentialRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await resources.CreateRegistryCredentialAsync(User.GetUserId(), User.GetTeamId(), request, cancellationToken));
    }
}
