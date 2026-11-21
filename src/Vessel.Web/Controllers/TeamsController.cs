using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Vessel.Application.Auth;
using Vessel.Application.Auth.Sessions;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Teams;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/teams")]
public sealed class TeamsController : ControllerBase
{
    private readonly AuthOptions _authOptions;
    private readonly VesselTeamService _teamService;

    public TeamsController(VesselTeamService teamService, IOptions<AuthOptions> authOptions)
    {
        _teamService = teamService;
        _authOptions = authOptions.Value;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<TeamSummary>> List()
    {
        return Ok(_teamService.ListTeams(User.GetUserId()));
    }

    [HttpPost("{teamId:guid}/switch")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<IActionResult> Switch(Guid teamId, CancellationToken cancellationToken)
    {
        var selectedTeamId = new TeamId(teamId);
        if (!_teamService.IsTeamMember(User.GetUserId(), selectedTeamId)) return Forbid();

        // IAM-09: the server-side session holds the active team (so a concurrent stale request cannot restore the old
        // one) and the user remembers it for the next login.
        if (User.GetSessionId() is not { } sessionId
            || !await HttpContext.RequestServices.GetRequiredService<UserSessionService>().SwitchTeamAsync(sessionId,
                User.GetUserId(), selectedTeamId, CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken))
            return Forbid();
        HttpContext.RequestServices.GetRequiredService<VesselSessionManager>().Forget(sessionId);

        // Re-issue the session from an explicit claim allow-list; never copy the caller's claims wholesale.
        await HttpContext.SignInAsync(
            VesselAuthenticationSchemes.Cookie,
            VesselSessionPrincipal.WithTeam(User, selectedTeamId));
        // Form posts from the Teams page land on the dashboard (Coolify v4.3.22); API callers get 204.
        return Request.HasFormContentType ? LocalRedirect("/") : NoContent();
    }

    [HttpGet("{teamId:guid}/members")]
    public ActionResult<IReadOnlyList<TeamMemberResponse>> Members(Guid teamId)
    {
        // Tokens are bound to their team, and sessions list the members of their active team only.
        if (new TeamId(teamId) != User.GetTeamId()) return Forbid();

        return Ok(_teamService.ListMembers(User.GetUserId(), new TeamId(teamId))
            .Select(member => new TeamMemberResponse(member.UserId.Value, member.Name, member.Email, member.Role,
                AccessGrantDto.From(member.Access)))
            .ToArray());
    }

    [HttpPost("{teamId:guid}/invitations")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<ActionResult<TeamInvitationResponse>> Invite(
        Guid teamId,
        TeamInvitationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            TeamInvitationResult result = await _teamService.InviteAsync(
                User.GetUserId(),
                new TeamId(teamId),
                request.Email,
                request.Role,
                request.Access?.ToGrants(),
                DateTimeOffset.UtcNow.AddDays(_authOptions.InvitationExpirationDays),
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);

            return Ok(new TeamInvitationResponse(result.InvitationId.Value, result.PlainTextToken,
                result.ExpiresAt));
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Replaces a member's access grants (IAM-05). Send <c>{ "access": null }</c> to restore the default.</summary>
    [HttpPut("{teamId:guid}/members/{userId:guid}/access")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<IActionResult> ChangeAccess(
        Guid teamId,
        Guid userId,
        ChangeAccessRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _teamService.SetMemberAccessAsync(
                User.GetUserId(),
                new TeamId(teamId),
                new UserId(userId),
                request.Access?.ToGrants(),
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DomainException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Form variant used by the invitation landing page: the emailed link only shows a confirm button.</summary>
    [HttpPost("/teams/invitations/accept")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<IActionResult> AcceptInvitationForm(
        [FromForm] AcceptInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _teamService.AcceptInvitationAsync(
            User.GetUserId(),
            request.Token,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        return LocalRedirect(accepted ? "/teams?status=joined" : "/invitations/accept?status=invalid");
    }

    [HttpPost("invitations/accept")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<IActionResult> AcceptInvitation(
        AcceptInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var accepted = await _teamService.AcceptInvitationAsync(
            User.GetUserId(),
            request.Token,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        return accepted ? NoContent() : BadRequest();
    }

    [HttpPut("{teamId:guid}/members/{userId:guid}/role")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<IActionResult> ChangeRole(
        Guid teamId,
        Guid userId,
        ChangeRoleRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _teamService.ChangeRoleAsync(
                User.GetUserId(),
                new TeamId(teamId),
                new UserId(userId),
                request.Role,
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
            return NoContent();
        }
        catch (DomainException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    [HttpDelete("{teamId:guid}/members/{userId:guid}")]
    [Authorize(Policy = VesselAuthorizationPolicies.InteractiveSession)]
    public async Task<IActionResult> RemoveMember(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await _teamService.RemoveMemberAsync(
                User.GetUserId(),
                new TeamId(teamId),
                new UserId(userId),
                CorrelationIdMiddleware.GetCorrelationId(HttpContext),
                cancellationToken);
            return NoContent();
        }
        catch (DomainException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

public sealed record TeamInvitationRequest(string Email, TeamRole Role, AccessRequestDto? Access = null);

public sealed record ChangeAccessRequest(AccessRequestDto? Access);

public sealed record TeamMemberResponse(
    Guid UserId,
    string Name,
    string Email,
    TeamRole Role,
    IReadOnlyList<AccessGrantDto>? Access);

public sealed record TeamInvitationResponse(Guid InvitationId, string Token, DateTimeOffset ExpiresAt);

public sealed record AcceptInvitationRequest(string Token);

public sealed record ChangeRoleRequest(TeamRole Role);
