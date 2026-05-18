using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Vessel.Application.Auth;
using Vessel.Domain;
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
    public async Task<IActionResult> Switch(Guid teamId)
    {
        var selectedTeamId = new TeamId(teamId);
        if (!_teamService.IsTeamMember(User.GetUserId(), selectedTeamId)) return Forbid();

        List<Claim> claims = User.Claims
            .Where(claim => claim.Type != VesselClaimTypes.TeamId)
            .Append(new Claim(VesselClaimTypes.TeamId, selectedTeamId.Value.ToString("D")))
            .ToList();

        await HttpContext.SignInAsync(
            VesselAuthenticationSchemes.Cookie,
            new ClaimsPrincipal(new ClaimsIdentity(claims, VesselAuthenticationSchemes.Cookie)));
        return NoContent();
    }

    [HttpPost("{teamId:guid}/invitations")]
    public async Task<ActionResult<TeamInvitationResponse>> Invite(
        Guid teamId,
        TeamInvitationRequest request,
        CancellationToken cancellationToken)
    {
        TeamInvitationResult result = await _teamService.InviteAsync(
            User.GetUserId(),
            new TeamId(teamId),
            request.Email,
            request.Role,
            DateTimeOffset.UtcNow.AddDays(_authOptions.InvitationExpirationDays),
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        return Ok(new TeamInvitationResponse(result.InvitationId.Value, result.PlainTextToken, result.ExpiresAt));
    }

    [HttpPost("invitations/accept")]
    public async Task<IActionResult> AcceptInvitation(
        AcceptInvitationRequest request,
        CancellationToken cancellationToken)
    {
        bool accepted = await _teamService.AcceptInvitationAsync(
            User.GetUserId(),
            request.Token,
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);

        return accepted ? NoContent() : BadRequest();
    }

    [HttpPut("{teamId:guid}/members/{userId:guid}/role")]
    public async Task<IActionResult> ChangeRole(
        Guid teamId,
        Guid userId,
        ChangeRoleRequest request,
        CancellationToken cancellationToken)
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

    [HttpDelete("{teamId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        await _teamService.RemoveMemberAsync(
            User.GetUserId(),
            new TeamId(teamId),
            new UserId(userId),
            CorrelationIdMiddleware.GetCorrelationId(HttpContext),
            cancellationToken);
        return NoContent();
    }
}

public sealed record TeamInvitationRequest(string Email, TeamRole Role);

public sealed record TeamInvitationResponse(Guid InvitationId, string Token, DateTimeOffset ExpiresAt);

public sealed record AcceptInvitationRequest(string Token);

public sealed record ChangeRoleRequest(TeamRole Role);
