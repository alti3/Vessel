using Vessel.Domain;
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
    IReadOnlySet<string> Permissions);

public sealed record CreatedToken(PersonalAccessTokenId Id, string PlainTextToken);

public sealed record TokenSummary(
    PersonalAccessTokenId Id,
    TeamId TeamId,
    string Name,
    IReadOnlySet<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record TwoFactorSetup(string Secret, string OtpAuthUri);

public sealed record TwoFactorRecoveryCodes(IReadOnlyList<string> Codes);

public sealed record TeamInvitationResult(
    TeamInvitationId InvitationId,
    string PlainTextToken,
    DateTimeOffset ExpiresAt);

public sealed record TeamSummary(TeamId Id, string Name, bool IsPersonal, TeamRole Role);
