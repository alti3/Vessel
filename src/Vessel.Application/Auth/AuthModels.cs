using Vessel.Domain;
using Vessel.Domain.Access;
using Vessel.Domain.Teams;

namespace Vessel.Application.Auth;

public sealed record AuthenticatedUser(UserId UserId, TeamId TeamId, string Name, string Email, bool TwoFactorRequired);

public sealed record AuthTokenValidationResult(
    bool Succeeded,
    UserId UserId,
    TeamId TeamId,
    PersonalAccessTokenId TokenId,
    string Name,
    string Email,
    IReadOnlySet<string> Scopes,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<AccessGrant>? Access = null);

public sealed record CreatedToken(PersonalAccessTokenId Id, string PlainTextToken);

public sealed record TokenSummary(
    PersonalAccessTokenId Id,
    TeamId TeamId,
    string Name,
    IReadOnlySet<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? PausedAt = null,
    IReadOnlyList<AccessGrant>? Access = null);

/// <summary>Edits a token's name, abilities, scope limits and expiry. The secret stays the same.</summary>
public sealed record UpdateTokenRequest(
    string Name,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<AccessGrant>? Access,
    DateTimeOffset? ExpiresAt);

public sealed record TeamMemberSummary(
    UserId UserId,
    string Name,
    string Email,
    TeamRole Role,
    IReadOnlyList<AccessGrant>? Access);

public sealed record TwoFactorSetup(string Secret, string OtpAuthUri);

public sealed record TwoFactorRecoveryCodes(IReadOnlyList<string> Codes);

public sealed record TeamInvitationResult(
    TeamInvitationId InvitationId,
    string PlainTextToken,
    DateTimeOffset ExpiresAt);

public sealed record TeamSummary(TeamId Id, string Name, bool IsPersonal, TeamRole Role);
