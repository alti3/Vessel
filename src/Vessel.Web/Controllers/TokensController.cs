using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Auth;
using Vessel.Domain;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/tokens")]
public sealed class TokensController : ControllerBase
{
    private readonly VesselTokenService _tokenService;

    public TokensController(VesselTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<TokenSummary>> List()
    {
        return Ok(_tokenService.List(User.GetUserId()));
    }

    [HttpPost]
    public async Task<ActionResult<CreateTokenResponse>> Create(
        CreateTokenRequest request,
        CancellationToken cancellationToken)
    {
        CreatedToken token = await _tokenService.CreateAsync(
            User.GetUserId(),
            User.GetTeamId(),
            request.Name,
            request.Scopes,
            request.ExpiresAt,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        return CreatedAtAction(nameof(List), new CreateTokenResponse(token.Id.Value, token.PlainTextToken));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var revoked = await _tokenService.RevokeAsync(
            User.GetUserId(),
            new PersonalAccessTokenId(id),
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        return revoked ? NoContent() : NotFound();
    }
}

public sealed record CreateTokenRequest(string Name, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt);

public sealed record CreateTokenResponse(Guid Id, string Token);
