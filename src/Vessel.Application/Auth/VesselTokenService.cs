using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Access;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;

namespace Vessel.Application.Auth;

/// <summary>
///     API tokens (IAM-04): abilities with the access-level hierarchy, optional scope limits (IAM-05), edit, pause,
///     resume and roll, and listing per active team. A token never exceeds its owner: abilities and scope limits are
///     checked against the owner's current role and grants when issued or edited, and again on every request.
/// </summary>
public sealed class VesselTokenService
{
    private readonly IAccessResolver _access;
    private readonly IAuditWriter _auditWriter;
    private readonly VesselAuthorizationService _ownerAuthorization;
    private readonly IApiTokenAccessContext _tokenContext;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IVesselDbContext _unitOfWork;

    public VesselTokenService(
        IVesselDbContext unitOfWork,
        ITokenGenerator tokenGenerator,
        IAuditWriter auditWriter,
        IAccessResolver access,
        IApiTokenAccessContext tokenContext)
    {
        _unitOfWork = unitOfWork;
        _tokenGenerator = tokenGenerator;
        _auditWriter = auditWriter;
        _access = access;
        _tokenContext = tokenContext;
        // Owner checks always evaluate the member layer only (never the presenting token).
        _ownerAuthorization = new VesselAuthorizationService(unitOfWork);
    }

    public Task<CreatedToken> CreateAsync(
        UserId userId,
        TeamId teamId,
        string name,
        IEnumerable<string> scopes,
        DateTimeOffset? expiresAt,
        IReadOnlySet<string>? presentingTokenScopes,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        return CreateAsync(userId, teamId, name, scopes, expiresAt, presentingTokenScopes, null, correlationId,
            cancellationToken);
    }

    /// <summary>Creates a token, optionally limited to <paramref name="accessGrants" /> (IAM-05 scope limits).</summary>
    public async Task<CreatedToken> CreateAsync(
        UserId userId,
        TeamId teamId,
        string name,
        IEnumerable<string> scopes,
        DateTimeOffset? expiresAt,
        IReadOnlySet<string>? presentingTokenScopes,
        IReadOnlyList<AccessGrant>? accessGrants,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TeamRole role = GetRole(userId, teamId)
                        ?? throw new UnauthorizedAccessException("User is not a member of this team.");
        var serializedScopes = TokenScopeMapper.Serialize(scopes);
        EnsureCanIssue(userId, teamId, role, TokenScopeMapper.Deserialize(serializedScopes), accessGrants,
            presentingTokenScopes);
        if (expiresAt.HasValue && expiresAt.Value <= now)
            throw new InvalidOperationException("Token expiration must be in the future.");

        var plainTextToken = _tokenGenerator.GenerateUrlSafeToken(30);
        var token = PersonalAccessToken.Create(
            userId,
            teamId,
            name,
            TokenHashing.Sha256(plainTextToken),
            serializedScopes,
            expiresAt,
            now,
            accessGrants);

        await _unitOfWork.PersonalAccessTokenRepository.AddAsync(token, cancellationToken);
        await AuditAsync(token, userId, AuditActions.TokenCreated, correlationId, cancellationToken,
            ("scopes", serializedScopes), ("access", VesselTeamService.DescribeAccess(token.AccessGrants)));
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreatedToken(token.Id, $"{token.Id.Value:N}|{plainTextToken}");
    }

    /// <summary>The caller's tokens for the active team only (Coolify v4.3.10 parity).</summary>
    public IReadOnlyList<TokenSummary> List(UserId userId, TeamId teamId)
    {
        return _unitOfWork.PersonalAccessTokens
            .Where(token => token.UserId == userId && token.TeamId == teamId)
            .OrderByDescending(token => token.CreatedAt)
            .AsEnumerable()
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<TokenSummary?> UpdateAsync(
        UserId userId,
        TeamId teamId,
        PersonalAccessTokenId tokenId,
        UpdateTokenRequest request,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        PersonalAccessToken? token = await GetOwnedAsync(userId, teamId, tokenId, cancellationToken);
        if (token is null) return null;

        TeamRole role = GetRole(userId, teamId)
                        ?? throw new UnauthorizedAccessException("User is not a member of this team.");
        var serializedScopes = TokenScopeMapper.Serialize(request.Scopes);
        EnsureCanIssue(userId, teamId, role, TokenScopeMapper.Deserialize(serializedScopes), request.Access, null);

        try
        {
            token.Update(request.Name, serializedScopes, request.Access, request.ExpiresAt, DateTimeOffset.UtcNow);
        }
        catch (DomainException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        await AuditAsync(token, userId, AuditActions.TokenUpdated, correlationId, cancellationToken,
            ("scopes", serializedScopes), ("access", VesselTeamService.DescribeAccess(token.AccessGrants)));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ToSummary(token);
    }

    public Task<TokenSummary?> PauseAsync(UserId userId, TeamId teamId, PersonalAccessTokenId tokenId,
        string? correlationId, CancellationToken cancellationToken = default)
    {
        return MutateAsync(userId, teamId, tokenId, AuditActions.TokenPaused, correlationId,
            (token, now) => token.Pause(now), cancellationToken);
    }

    public Task<TokenSummary?> ResumeAsync(UserId userId, TeamId teamId, PersonalAccessTokenId tokenId,
        string? correlationId, CancellationToken cancellationToken = default)
    {
        return MutateAsync(userId, teamId, tokenId, AuditActions.TokenResumed, correlationId,
            (token, now) => token.Resume(now), cancellationToken);
    }

    /// <summary>Issues a new secret for the token and keeps its name, abilities, scope limits and expiry.</summary>
    public async Task<CreatedToken?> RollAsync(
        UserId userId,
        TeamId teamId,
        PersonalAccessTokenId tokenId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        PersonalAccessToken? token = await GetOwnedAsync(userId, teamId, tokenId, cancellationToken);
        if (token is null) return null;

        var plainTextToken = _tokenGenerator.GenerateUrlSafeToken(30);
        try
        {
            token.Roll(TokenHashing.Sha256(plainTextToken), DateTimeOffset.UtcNow);
        }
        catch (DomainException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        await AuditAsync(token, userId, AuditActions.TokenRolled, correlationId, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new CreatedToken(token.Id, $"{token.Id.Value:N}|{plainTextToken}");
    }

    public async Task<bool> RevokeAsync(
        UserId userId,
        PersonalAccessTokenId tokenId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        PersonalAccessToken? token = await _unitOfWork.PersonalAccessTokenRepository.GetByIdAsync(
            tokenId,
            cancellationToken);
        if (token is null || token.UserId != userId) return false;

        token.Revoke(DateTimeOffset.UtcNow);
        await AuditAsync(token, userId, AuditActions.TokenRevoked, correlationId, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<AuthTokenValidationResult> ValidateAsync(
        string presentedToken,
        CancellationToken cancellationToken = default)
    {
        var parts = presentedToken.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out Guid tokenGuid))
            return Failed();

        var tokenId = new PersonalAccessTokenId(tokenGuid);
        PersonalAccessToken? token = await _unitOfWork.PersonalAccessTokenRepository.GetByIdAsync(
            tokenId,
            cancellationToken);
        // Revoked, expired and paused tokens cannot authenticate.
        if (token is null || !token.IsActive(DateTimeOffset.UtcNow)) return Failed();

        var hash = TokenHashing.Sha256(parts[1]);
        if (!string.Equals(token.TokenHash, hash, StringComparison.Ordinal)) return Failed();

        User? user = await _unitOfWork.UserRepository.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null) return Failed();

        // The token owner must still be a member of the token's team. Removing a member invalidates every token
        // they issued for that team, even if the token itself was never revoked.
        TeamRole? role = GetRole(token.UserId, token.TeamId);
        if (!role.HasValue) return Failed();

        // Effective permissions are the token's abilities clamped to what the owner holds today (role and grants),
        // so a demoted owner's token can never exceed what the owner may do now.
        IReadOnlySet<string> scopes = TokenScopeMapper.Deserialize(token.Scopes);
        IReadOnlySet<string> permissions = TokenScopeMapper.ToPermissions(scopes)
            .Where(permission => _ownerAuthorization.HasPermissionSomewhere(token.UserId, token.TeamId, permission))
            .ToHashSet(StringComparer.Ordinal);
        token.MarkUsed(DateTimeOffset.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new AuthTokenValidationResult(
            true,
            user.Id,
            token.TeamId,
            token.Id,
            user.Name.Value,
            user.Email.Value,
            scopes,
            permissions,
            token.AccessGrants);
    }

    /// <summary>
    ///     A token can never carry more abilities than its issuer: <c>root</c> and <c>write:sensitive</c> are reserved
    ///     for owners and admins; for members every requested level must be held at every requested scope; scope
    ///     limits must point inside the team; and a token-authenticated caller must hold <c>root</c>, have no scope
    ///     limits itself, and may only mint abilities its own token covers.
    /// </summary>
    private void EnsureCanIssue(
        UserId userId,
        TeamId teamId,
        TeamRole role,
        IReadOnlySet<string> requestedScopes,
        IReadOnlyList<AccessGrant>? accessGrants,
        IReadOnlySet<string>? presentingTokenScopes)
    {
        var privileged = role is TeamRole.Owner or TeamRole.Admin;
        IReadOnlySet<string>? callerPermissions = null;
        if (presentingTokenScopes is not null)
        {
            if (!presentingTokenScopes.Contains(TokenScopeMapper.Root))
                throw new UnauthorizedAccessException(
                    "Creating tokens requires an interactive session or a token with the root ability.");
            if (_tokenContext.Current?.Grants is not null)
                throw new UnauthorizedAccessException("A token limited to selected scopes cannot create tokens.");
            callerPermissions = TokenScopeMapper.ToPermissions(presentingTokenScopes);
        }

        if (accessGrants is not null)
        {
            if (accessGrants.Count == 0)
                throw new InvalidOperationException("A scoped token needs at least one scope.");
            if (accessGrants.Count > AccessGrant.MaxGrantsPerActor)
                throw new InvalidOperationException(
                    $"At most {AccessGrant.MaxGrantsPerActor} token scopes are allowed.");
            if (accessGrants.Any(grant => _access.LocateGrant(teamId, grant) is null))
                throw new InvalidOperationException(
                    "Every token scope must point at this team or one of its projects, environments or resources.");
        }

        foreach (var scope in requestedScopes)
        {
            if (!privileged && TokenScopeMapper.PrivilegedScopes.Contains(scope))
                throw new UnauthorizedAccessException($"Token scope '{scope}' requires the owner or admin role.");

            IReadOnlySet<string> scopePermissions = TokenScopeMapper.ToPermissions([scope]);
            if (callerPermissions is not null && !scopePermissions.All(callerPermissions.Contains))
                throw new UnauthorizedAccessException($"Token scope '{scope}' exceeds the presenting token.");
        }

        // The token's abilities applied to its scopes (or the whole team) must be held by the owner (IAM-05).
        IReadOnlySet<AccessLevel> levels = TokenScopeMapper.ToLevels(requestedScopes);
        IReadOnlyList<AccessGrant> effective = new ApiTokenAccess(default, userId, teamId, requestedScopes,
            accessGrants).AsGrants();
        if (levels.Count > 0 && !_access.MemberHolds(userId, teamId, effective))
            throw new UnauthorizedAccessException("The token's abilities exceed your role and access in this team.");
    }

    private async Task<TokenSummary?> MutateAsync(
        UserId userId,
        TeamId teamId,
        PersonalAccessTokenId tokenId,
        string auditAction,
        string? correlationId,
        Action<PersonalAccessToken, DateTimeOffset> mutate,
        CancellationToken cancellationToken)
    {
        PersonalAccessToken? token = await GetOwnedAsync(userId, teamId, tokenId, cancellationToken);
        if (token is null) return null;

        try
        {
            mutate(token, DateTimeOffset.UtcNow);
        }
        catch (DomainException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        await AuditAsync(token, userId, auditAction, correlationId, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ToSummary(token);
    }

    private async Task<PersonalAccessToken?> GetOwnedAsync(
        UserId userId,
        TeamId teamId,
        PersonalAccessTokenId tokenId,
        CancellationToken cancellationToken)
    {
        PersonalAccessToken? token = await _unitOfWork.PersonalAccessTokenRepository.GetByIdAsync(tokenId,
            cancellationToken);
        return token is not null && token.UserId == userId && token.TeamId == teamId ? token : null;
    }

    private Task AuditAsync(
        PersonalAccessToken token,
        UserId actorUserId,
        string action,
        string? correlationId,
        CancellationToken cancellationToken,
        params (string Key, object? Value)[] details)
    {
        var metadata = new Dictionary<string, object?> { ["name"] = token.Name };
        foreach ((var key, var value) in details) metadata[key] = value;

        // IAM-08: staged, so the token change and its audit row commit in the caller's save.
        return _auditWriter.StageAsync(new AuditEntry(token.TeamId, actorUserId, action,
            new AuditTarget("personal_access_token", token.Id.ToString()))
        {
            CorrelationId = correlationId,
            ResourceName = token.Name,
            Metadata = metadata
        }, cancellationToken);
    }

    private static TokenSummary ToSummary(PersonalAccessToken token)
    {
        return new TokenSummary(
            token.Id,
            token.TeamId,
            token.Name,
            TokenScopeMapper.Deserialize(token.Scopes),
            token.CreatedAt,
            token.ExpiresAt,
            token.LastUsedAt,
            token.RevokedAt,
            token.PausedAt,
            token.AccessGrants);
    }

    private TeamRole? GetRole(UserId userId, TeamId teamId)
    {
        return _unitOfWork.TeamMemberships
            .Where(membership => membership.UserId == userId && membership.TeamId == teamId)
            .Select(membership => (TeamRole?)membership.Role)
            .SingleOrDefault();
    }

    private static AuthTokenValidationResult Failed()
    {
        return new AuthTokenValidationResult(
            false,
            default,
            default,
            default,
            string.Empty,
            string.Empty,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }
}
