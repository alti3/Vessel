using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;

namespace Vessel.Application.Auth;

public sealed class VesselTokenService
{
    private readonly IAuditWriter _auditWriter;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IVesselDbContext _unitOfWork;

    public VesselTokenService(
        IVesselDbContext unitOfWork,
        ITokenGenerator tokenGenerator,
        IAuditWriter auditWriter)
    {
        _unitOfWork = unitOfWork;
        _tokenGenerator = tokenGenerator;
        _auditWriter = auditWriter;
    }

    public async Task<CreatedToken> CreateAsync(
        UserId userId,
        TeamId teamId,
        string name,
        IEnumerable<string> scopes,
        DateTimeOffset? expiresAt,
        IReadOnlySet<string>? presentingTokenScopes,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TeamRole role = GetRole(userId, teamId)
                        ?? throw new UnauthorizedAccessException("User is not a member of this team.");
        var serializedScopes = TokenScopeMapper.Serialize(scopes);
        EnsureCanIssue(role, TokenScopeMapper.Deserialize(serializedScopes), presentingTokenScopes);
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
            now);

        await _unitOfWork.PersonalAccessTokenRepository.AddAsync(token, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            teamId,
            userId,
            AuditActions.TokenCreated,
            new AuditTarget("personal_access_token", token.Id.ToString()),
            correlationId,
            new Dictionary<string, object?>
            {
                ["name"] = token.Name,
                ["scopes"] = serializedScopes
            },
            cancellationToken);

        return new CreatedToken(token.Id, $"{token.Id.Value:N}|{plainTextToken}");
    }

    public IReadOnlyList<TokenSummary> List(UserId userId)
    {
        return _unitOfWork.PersonalAccessTokens
            .Where(token => token.UserId == userId)
            .OrderByDescending(token => token.CreatedAt)
            .Select(token => new TokenSummary(
                token.Id,
                token.TeamId,
                token.Name,
                TokenScopeMapper.Deserialize(token.Scopes),
                token.CreatedAt,
                token.ExpiresAt,
                token.LastUsedAt,
                token.RevokedAt))
            .ToArray();
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
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            token.TeamId,
            userId,
            AuditActions.TokenRevoked,
            new AuditTarget("personal_access_token", token.Id.ToString()),
            correlationId,
            new Dictionary<string, object?> { ["name"] = token.Name },
            cancellationToken);

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
        if (token is null || !token.IsActive(DateTimeOffset.UtcNow)) return Failed();

        var hash = TokenHashing.Sha256(parts[1]);
        if (!string.Equals(token.TokenHash, hash, StringComparison.Ordinal)) return Failed();

        User? user = await _unitOfWork.UserRepository.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null) return Failed();

        // The token owner must still be a member of the token's team. Removing a member invalidates every token
        // they issued for that team, even if the token itself was never revoked.
        TeamRole? role = GetRole(token.UserId, token.TeamId);
        if (!role.HasValue) return Failed();

        // Effective permissions are the token's scopes clamped to the owner's current role, so a demoted owner's
        // token can never exceed what the owner may do today.
        IReadOnlySet<string> scopes = TokenScopeMapper.Deserialize(token.Scopes);
        IReadOnlySet<string> rolePermissions = VesselPermissions.ForRole(role.Value);
        IReadOnlySet<string> permissions = TokenScopeMapper.ToPermissions(scopes)
            .Where(rolePermissions.Contains)
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
            permissions);
    }

    /// <summary>
    ///     A token can never carry more abilities than its issuer: every requested scope must be covered by the
    ///     issuer's role in the team, <c>root</c> and <c>write:sensitive</c> are reserved for owners and admins, and a
    ///     token-authenticated caller must hold <c>root</c> and may only mint scopes its own token covers.
    /// </summary>
    private static void EnsureCanIssue(
        TeamRole role,
        IReadOnlySet<string> requestedScopes,
        IReadOnlySet<string>? presentingTokenScopes)
    {
        IReadOnlySet<string> rolePermissions = VesselPermissions.ForRole(role);
        bool privileged = role is TeamRole.Owner or TeamRole.Admin;
        IReadOnlySet<string>? callerPermissions = null;
        if (presentingTokenScopes is not null)
        {
            if (!presentingTokenScopes.Contains(TokenScopeMapper.Root))
                throw new UnauthorizedAccessException(
                    "Creating tokens requires an interactive session or a token with the root ability.");
            callerPermissions = TokenScopeMapper.ToPermissions(presentingTokenScopes);
        }

        foreach (var scope in requestedScopes)
        {
            if (!privileged && scope is TokenScopeMapper.Root or TokenScopeMapper.WriteSensitive)
                throw new UnauthorizedAccessException($"Token scope '{scope}' requires the owner or admin role.");

            IReadOnlySet<string> scopePermissions = TokenScopeMapper.ToPermissions([scope]);
            if (!scopePermissions.All(rolePermissions.Contains))
                throw new UnauthorizedAccessException($"Token scope '{scope}' exceeds your role in this team.");
            if (callerPermissions is not null && !scopePermissions.All(callerPermissions.Contains))
                throw new UnauthorizedAccessException($"Token scope '{scope}' exceeds the presenting token.");
        }
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
