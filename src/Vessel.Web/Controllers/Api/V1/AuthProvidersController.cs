using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Auth.External;
using Vessel.Application.Authorization;
using Vessel.Domain;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     External sign-in providers (IAM-07): generic OIDC plus GitHub, GitLab and Google presets. Instance administrators
///     only, interactive sessions only. Client secrets are write-only (protected with the key ring, never returned).
/// </summary>
[ApiController]
[Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
[Authorize(Policy = VesselPermissions.InstanceManage)]
[Route("api/v1/instance/auth-providers")]
public sealed class AuthProvidersController(AuthProviderAdminService providers) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<AuthProviderSummary>> List()
    {
        return Ok(providers.List(User.GetUserId()));
    }

    [HttpPost]
    public async Task<ActionResult<AuthProviderSummary>> Create(AuthProviderInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await providers.CreateAsync(User.GetUserId(), input,
                CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpPut("{providerId:guid}")]
    public async Task<ActionResult<AuthProviderSummary>> Update(Guid providerId, AuthProviderInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            AuthProviderSummary? updated = await providers.UpdateAsync(User.GetUserId(), new AuthProviderId(providerId),
                input, CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpDelete("{providerId:guid}")]
    public async Task<IActionResult> Delete(Guid providerId, CancellationToken cancellationToken)
    {
        return await providers.DeleteAsync(User.GetUserId(), new AuthProviderId(providerId),
            CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken)
            ? NoContent()
            : NotFound();
    }
}
