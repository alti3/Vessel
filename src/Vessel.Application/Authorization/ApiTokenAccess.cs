using Vessel.Domain;
using Vessel.Domain.Access;

namespace Vessel.Application.Authorization;

/// <summary>
///     The API token that authenticated the current request: its owner, team, abilities and optional scope limits.
///     Application authorization intersects this "token layer" with the owner's member layer, so a token never
///     exceeds its owner and stops working on anything outside its scope limits.
/// </summary>
public sealed record ApiTokenAccess(
    PersonalAccessTokenId TokenId,
    UserId UserId,
    TeamId TeamId,
    IReadOnlySet<string> Abilities,
    IReadOnlyList<AccessGrant>? Grants)
{
    public IReadOnlySet<AccessLevel> Levels { get; } = TokenScopeMapper.ToLevels(Abilities);

    /// <summary>The token as a grant list: its scope limits (or the whole team) with its abilities as levels.</summary>
    public IReadOnlyList<AccessGrant> AsGrants()
    {
        if (Grants is null) return [new AccessGrant(AccessScope.Team, null, Levels)];
        return Grants.Select(grant => new AccessGrant(grant.Scope, grant.TargetId, Levels)).ToArray();
    }
}

/// <summary>
///     Supplies the API token of the current request, or <c>null</c> for interactive sessions, background jobs and
///     webhooks. Implemented by the Web host from the authenticated bearer principal.
/// </summary>
public interface IApiTokenAccessContext
{
    ApiTokenAccess? Current { get; }
}

/// <summary>No token layer (non-HTTP hosts, background jobs, tests).</summary>
public sealed class NoApiTokenAccessContext : IApiTokenAccessContext
{
    public static readonly NoApiTokenAccessContext Instance = new();

    public ApiTokenAccess? Current => null;
}
