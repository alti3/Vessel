using Vessel.Application.Auditing;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Auth;

public sealed class VesselTeamService
{
    private readonly IAuditWriter _auditWriter;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IVesselDbContext _unitOfWork;

    public VesselTeamService(
        IVesselDbContext unitOfWork,
        ITokenGenerator tokenGenerator,
        IAuditWriter auditWriter)
    {
        _unitOfWork = unitOfWork;
        _tokenGenerator = tokenGenerator;
        _auditWriter = auditWriter;
    }

    public IReadOnlyList<TeamSummary> ListTeams(UserId userId)
    {
        return _unitOfWork.TeamMemberships
            .Where(membership => membership.UserId == userId)
            .Join(
                _unitOfWork.Teams,
                membership => membership.TeamId,
                team => team.Id,
                (membership, team) => new TeamSummary(team.Id, team.Name.Value, team.IsPersonal, membership.Role))
            .OrderBy(team => team.Name)
            .ToArray();
    }

    public async Task<TeamInvitationResult> InviteAsync(
        UserId actorUserId,
        TeamId teamId,
        string email,
        TeamRole role,
        DateTimeOffset expiresAt,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        EnsureCanManageTeam(actorUserId, teamId);
        var plainTextToken = _tokenGenerator.GenerateUrlSafeToken();
        var invitation = TeamInvitation.Create(
            teamId,
            new EmailAddress(email),
            role,
            TokenHashing.Sha256(plainTextToken),
            expiresAt,
            DateTimeOffset.UtcNow);

        await _unitOfWork.TeamInvitationRepository.AddAsync(invitation, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            teamId,
            actorUserId,
            AuditActions.TeamInvitationCreated,
            new AuditTarget("team_invitation", invitation.Id.ToString()),
            correlationId,
            new Dictionary<string, object?>
            {
                ["email"] = invitation.Email.Value,
                ["role"] = invitation.Role.ToString()
            },
            cancellationToken);

        return new TeamInvitationResult(invitation.Id, $"{invitation.Id.Value:N}|{plainTextToken}",
            invitation.ExpiresAt);
    }

    public async Task<bool> AcceptInvitationAsync(
        UserId userId,
        string presentedToken,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var parts = presentedToken.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out Guid invitationGuid)) return false;

        TeamInvitation? invitation = await _unitOfWork.TeamInvitationRepository.GetByIdAsync(
            new TeamInvitationId(invitationGuid),
            cancellationToken);
        User? user = await _unitOfWork.UserRepository.GetByIdAsync(userId, cancellationToken);
        if (invitation is null
            || user is null
            || !invitation.IsValid(DateTimeOffset.UtcNow)
            || invitation.Email != user.Email
            || !string.Equals(invitation.TokenHash, TokenHashing.Sha256(parts[1]), StringComparison.Ordinal))
            return false;

        Team team = await _unitOfWork.TeamRepository.GetByIdAsync(invitation.TeamId, cancellationToken)
                    ?? throw new InvalidOperationException("Invitation team was not found.");
        if (!team.Memberships.Any(membership => membership.UserId == userId))
            team.AddMember(userId, invitation.Role, DateTimeOffset.UtcNow);
        invitation.Accept(DateTimeOffset.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            team.Id,
            userId,
            AuditActions.TeamInvitationAccepted,
            new AuditTarget("team_invitation", invitation.Id.ToString()),
            correlationId,
            new Dictionary<string, object?>(),
            cancellationToken);

        return true;
    }

    public async Task ChangeRoleAsync(
        UserId actorUserId,
        TeamId teamId,
        UserId memberUserId,
        TeamRole role,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        EnsureCanManageTeam(actorUserId, teamId);
        Team team = await GetTeamAsync(teamId, cancellationToken);
        team.ChangeMemberRole(memberUserId, role, DateTimeOffset.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            teamId,
            actorUserId,
            AuditActions.TeamMemberRoleChanged,
            new AuditTarget("user", memberUserId.ToString()),
            correlationId,
            new Dictionary<string, object?> { ["role"] = role.ToString() },
            cancellationToken);
    }

    public async Task RemoveMemberAsync(
        UserId actorUserId,
        TeamId teamId,
        UserId memberUserId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        EnsureCanManageTeam(actorUserId, teamId);
        Team team = await GetTeamAsync(teamId, cancellationToken);
        team.RemoveMember(memberUserId, DateTimeOffset.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            teamId,
            actorUserId,
            AuditActions.TeamMemberRemoved,
            new AuditTarget("user", memberUserId.ToString()),
            correlationId,
            new Dictionary<string, object?>(),
            cancellationToken);
    }

    public bool IsTeamMember(UserId userId, TeamId teamId)
    {
        return _unitOfWork.TeamMemberships.Any(membership =>
            membership.UserId == userId && membership.TeamId == teamId);
    }

    private void EnsureCanManageTeam(UserId userId, TeamId teamId)
    {
        TeamRole? role = _unitOfWork.TeamMemberships
            .Where(membership => membership.UserId == userId && membership.TeamId == teamId)
            .Select(membership => (TeamRole?)membership.Role)
            .SingleOrDefault();

        if (role is not (TeamRole.Owner or TeamRole.Admin))
            throw new UnauthorizedAccessException("User cannot manage this team.");
    }

    private async Task<Team> GetTeamAsync(TeamId teamId, CancellationToken cancellationToken)
    {
        return await _unitOfWork.TeamRepository.GetByIdAsync(teamId, cancellationToken)
               ?? throw new InvalidOperationException("Team was not found.");
    }
}
