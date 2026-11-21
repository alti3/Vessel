using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Auth;
using Vessel.Domain;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers;

/// <summary>
///     API tokens of the caller in the active team (IAM-04). Listing and creation also accept a <c>root</c> token;
///     editing, pausing, resuming and rolling are interactive-session only.
/// </summary>
[ApiController]
[Authorize(Policy = VesselAuthorizationPolicies.InteractiveSessionOrRootToken)]
[Route("api/tokens")]
public sealed class TokensController : ControllerBase
{
    private readonly VesselTokenService _tokenService;

    public TokensController(VesselTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<TokenResponse>> List()
    {
        return Ok(_tokenService.List(User.GetUserId(), User.GetTeamId()).Select(TokenResponse.From).ToArray());
    }

    [HttpPost]
    public async Task<ActionResult<CreateTokenResponse>> Create(
        CreateTokenRequest request,
        CancellationToken cancellationToken)
    {
        CreatedToken token;
        try
        {
            // A token-authenticated caller passes its own scopes so the service can refuse to mint a broader token.
            token = await _tokenService.CreateAsync(
                User.GetUserId(),
                User.GetTeamId(),
                request.Name,
                request.Scopes ?? [],
                request.ExpiresAt,
                User.GetTokenScopes(),
                request.Access?.ToGrants(),
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        return CreatedAtAction(nameof(List), new CreateTokenResponse(token.Id.Value, token.PlainTextToken));
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<ActionResult<TokenResponse>> Update(
        Guid id,
        UpdateTokenApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            TokenSummary? token = await _tokenService.UpdateAsync(
                User.GetUserId(),
                User.GetTeamId(),
                new PersonalAccessTokenId(id),
                new UpdateTokenRequest(request.Name, request.Scopes ?? [], request.Access?.ToGrants(),
                    request.ExpiresAt),
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
            return token is null ? NotFound() : Ok(TokenResponse.From(token));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpPost("{id:guid}/pause")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public Task<ActionResult<TokenResponse>> Pause(Guid id, CancellationToken cancellationToken)
    {
        return MutateAsync(() => _tokenService.PauseAsync(User.GetUserId(), User.GetTeamId(),
            new PersonalAccessTokenId(id), CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken));
    }

    [HttpPost("{id:guid}/resume")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public Task<ActionResult<TokenResponse>> Resume(Guid id, CancellationToken cancellationToken)
    {
        return MutateAsync(() => _tokenService.ResumeAsync(User.GetUserId(), User.GetTeamId(),
            new PersonalAccessTokenId(id), CorrelationIdMiddleware.GetCorrelationId(HttpContext), cancellationToken));
    }

    /// <summary>Issues a new secret and keeps the token's metadata. The old secret stops working immediately.</summary>
    [HttpPost("{id:guid}/roll")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<ActionResult<CreateTokenResponse>> Roll(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            CreatedToken? token = await _tokenService.RollAsync(User.GetUserId(), User.GetTeamId(),
                new PersonalAccessTokenId(id), CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
            return token is null ? NotFound() : Ok(new CreateTokenResponse(token.Id.Value, token.PlainTextToken));
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
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

    private async Task<ActionResult<TokenResponse>> MutateAsync(Func<Task<TokenSummary?>> mutate)
    {
        try
        {
            TokenSummary? token = await mutate();
            return token is null ? NotFound() : Ok(TokenResponse.From(token));
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

public sealed record CreateTokenRequest(
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    AccessRequestDto? Access = null);

public sealed record UpdateTokenApiRequest(
    string Name,
    IReadOnlyList<string>? Scopes,
    DateTimeOffset? ExpiresAt,
    AccessRequestDto? Access = null);

public sealed record CreateTokenResponse(Guid Id, string Token);

public sealed record TokenResponse(
    Guid Id,
    Guid TeamId,
    string Name,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<AccessGrantDto>? Access,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? PausedAt)
{
    public static TokenResponse From(TokenSummary token)
    {
        return new TokenResponse(token.Id.Value, token.TeamId.Value, token.Name, token.Scopes.Order().ToArray(),
            AccessGrantDto.From(token.Access), token.CreatedAt, token.ExpiresAt, token.LastUsedAt, token.RevokedAt,
            token.PausedAt);
    }
}
