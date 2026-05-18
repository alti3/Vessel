using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
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
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        EnsureTeamMember(userId, teamId);
        string serializedScopes = TokenScopeMapper.Serialize(scopes);
        string plainTextToken = _tokenGenerator.GenerateUrlSafeToken(30);
        var token = PersonalAccessToken.Create(
            userId,
            teamId,
            name,
            TokenHashing.Sha256(plainTextToken),
            serializedScopes,
            expiresAt,
            DateTimeOffset.UtcNow);

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
        string[] parts = presentedToken.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out Guid tokenGuid))
            return Failed();

        var tokenId = new PersonalAccessTokenId(tokenGuid);
        PersonalAccessToken? token = await _unitOfWork.PersonalAccessTokenRepository.GetByIdAsync(
            tokenId,
            cancellationToken);
        if (token is null || !token.IsActive(DateTimeOffset.UtcNow)) return Failed();

        string hash = TokenHashing.Sha256(parts[1]);
        if (!string.Equals(token.TokenHash, hash, StringComparison.Ordinal)) return Failed();

        User? user = await _unitOfWork.UserRepository.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null) return Failed();

        IReadOnlySet<string> scopes = TokenScopeMapper.Deserialize(token.Scopes);
        IReadOnlySet<string> permissions = TokenScopeMapper.ToPermissions(scopes);
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

    private void EnsureTeamMember(UserId userId, TeamId teamId)
    {
        if (!_unitOfWork.TeamMemberships.Any(membership => membership.UserId == userId && membership.TeamId == teamId))
            throw new InvalidOperationException("User is not a member of this team.");
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
