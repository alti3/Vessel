using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Auth.Sessions;
using Vessel.Application.Authorization;
using Vessel.Application.Instance;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Auth;

public sealed class VesselAuthenticationService
{
    private readonly IAuditWriter _auditWriter;
    private readonly AuthEmailService? _authEmails;
    private readonly AuthOptions _options;
    private readonly IPasswordHasher _passwordHasher;
    private readonly UserSessionService? _sessions;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly TotpService _totpService;
    private readonly TwoFactorSecretProtector _twoFactorSecrets;
    private readonly IVesselDbContext _unitOfWork;

    public VesselAuthenticationService(
        IVesselDbContext unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenGenerator tokenGenerator,
        TotpService totpService,
        IAuditWriter auditWriter,
        IOptions<AuthOptions> options,
        ISecretCipher secretCipher,
        AuthEmailService? authEmails = null,
        UserSessionService? sessions = null)
    {
        _sessions = sessions;
        _authEmails = authEmails;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _totpService = totpService;
        _twoFactorSecrets = new TwoFactorSecretProtector(secretCipher);
        _auditWriter = auditWriter;
        _options = options.Value;
    }

    public async Task<AuthenticatedUser> RegisterAsync(
        string name,
        string email,
        string password,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        PasswordPolicy.Validate(password);
        var emailAddress = new EmailAddress(email);
        // IAM-06: self-service registration is closed once the first (bootstrap) account exists, unless an instance
        // administrator opens it or the email holds a pending invitation.
        if (!new InstanceSettingsService(_unitOfWork, new VesselAuthorizationService(_unitOfWork), _auditWriter,
                TimeProvider.System).CanRegister(emailAddress.Value))
        {
            await _auditWriter.RecordAsync(new AuditEntry(null, null, AuditActions.RegistrationRejected,
                AnonymousSubject(emailAddress.Value))
            {
                CorrelationId = correlationId,
                Metadata = new Dictionary<string, object?> { ["method"] = "password" }
            }, cancellationToken);
            throw new RegistrationClosedException();
        }

        if (_unitOfWork.Users.Any(user => user.Email == emailAddress))
            throw new InvalidOperationException("A user with this email already exists.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var isFirstUser = !_unitOfWork.Users.Any();
        var user = User.Create(new DisplayName(name), emailAddress, now);
        user.SetPasswordHash(_passwordHasher.HashPassword(password), false, now);
        // Mirrors Coolify's root user: the first account on an instance is the instance administrator.
        // Later self-registered users never receive host-local privileges implicitly.
        if (isFirstUser) user.GrantInstanceAdmin(now);
        var team = Team.Create(new DisplayName($"{user.Name.Value}'s Team"), user.Id, true, now);
        user.SetCurrentTeam(team.Id);

        await _unitOfWork.UserRepository.AddAsync(user, cancellationToken);
        await _unitOfWork.TeamRepository.AddAsync(team, cancellationToken);
        // IAM-08: the audit row commits with the account (same save); no raw email in metadata.
        await _auditWriter.StageAsync(new AuditEntry(team.Id, user.Id, AuditActions.UserRegistered,
            new AuditTarget("user", user.Id.ToString()))
        {
            CorrelationId = correlationId,
            ResourceName = user.Name.Value,
            Metadata = new Dictionary<string, object?> { ["method"] = "password", ["instanceAdmin"] = isFirstUser }
        }, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // IAM-10: verification mail when instance email is configured (the first user verifies later from Profile).
        if (!isFirstUser) _authEmails?.QueueEmailVerification(user);

        return new AuthenticatedUser(user.Id, team.Id, user.Name.Value, user.Email.Value, false);
    }

    public async Task<AuthenticatedUser?> LoginAsync(
        string email,
        string password,
        string? twoFactorCode,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var emailAddress = new EmailAddress(email);
        User? user = _unitOfWork.Users.SingleOrDefault(candidate => candidate.Email == emailAddress);
        if (user is null || !user.HasPassword)
        {
            await AuditFailedLoginAsync(emailAddress.Value, correlationId, cancellationToken, user?.Id,
                user is null ? "unknown_account" : "no_password");
            return null;
        }

        if (user.IsLockedOut(now))
        {
            await AuditFailedLoginAsync(emailAddress.Value, correlationId, cancellationToken, user.Id, "locked_out");
            return null;
        }

        if (!_passwordHasher.VerifyPassword(user.PasswordHash!, password))
        {
            user.RecordFailedLogin(
                Math.Max(1, _options.LockoutThreshold),
                TimeSpan.FromMinutes(Math.Max(1, _options.LockoutMinutes)),
                now);
            // The failure counter and its audit row commit together.
            await StageFailedLoginAsync(user, "invalid_password", correlationId, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (user.TwoFactorConfirmedAt.HasValue)
        {
            if (string.IsNullOrWhiteSpace(twoFactorCode))
                return new AuthenticatedUser(user.Id, default, user.Name.Value, user.Email.Value, true);

            if (!VerifySecondFactor(user, twoFactorCode, now))
            {
                await AuditFailedLoginAsync(emailAddress.Value, correlationId, cancellationToken, user.Id,
                    "invalid_second_factor");
                return null;
            }
        }

        return await CompleteLoginAsync(user, user.TwoFactorConfirmedAt.HasValue ? "password+2fa" : "password",
            correlationId, now, cancellationToken);
    }

    /// <summary>
    ///     Second step of an external (OAuth/OIDC) sign-in for accounts with two-factor authentication (IAM-07): the
    ///     provider already proved the identity, so only the TOTP or a recovery code is checked. Wrong codes count
    ///     towards the lockout like password failures.
    /// </summary>
    public async Task<AuthenticatedUser?> CompleteExternalTwoFactorAsync(
        UserId userId,
        string authenticationMethod,
        string? twoFactorCode,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        User? user = await _unitOfWork.UserRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null || !user.TwoFactorConfirmedAt.HasValue || user.IsLockedOut(now)) return null;

        if (string.IsNullOrWhiteSpace(twoFactorCode) || !VerifySecondFactor(user, twoFactorCode, now))
        {
            user.RecordFailedLogin(
                Math.Max(1, _options.LockoutThreshold),
                TimeSpan.FromMinutes(Math.Max(1, _options.LockoutMinutes)),
                now);
            await StageFailedLoginAsync(user, "invalid_second_factor", correlationId, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }

        return await CompleteLoginAsync(user, authenticationMethod + "+2fa", correlationId, now, cancellationToken);
    }

    /// <summary>
    ///     Finishes a verified sign-in: resolves the landing team (IAM-09: the remembered team while still a member,
    ///     otherwise the personal team; never a team the user is merely invited to), resets the failure counter and
    ///     records the login in the same save.
    /// </summary>
    internal async Task<AuthenticatedUser> CompleteLoginAsync(
        User user,
        string authenticationMethod,
        string? correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        TeamId teamId = await UserSessionService.ResolveOrCreateHomeTeamAsync(_unitOfWork, user, now,
            cancellationToken);
        user.SetCurrentTeam(teamId);
        user.RecordSuccessfulLogin(now);
        await _auditWriter.StageAsync(new AuditEntry(teamId, user.Id,
            authenticationMethod.StartsWith("oauth", StringComparison.Ordinal)
                ? AuditActions.OAuthLoginSucceeded
                : AuditActions.UserLoggedIn,
            new AuditTarget("user", user.Id.ToString()))
        {
            CorrelationId = correlationId,
            ResourceName = user.Name.Value,
            Metadata = new Dictionary<string, object?> { ["method"] = authenticationMethod }
        }, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new AuthenticatedUser(user.Id, teamId, user.Name.Value, user.Email.Value, false);
    }

    private bool VerifySecondFactor(User user, string code, DateTimeOffset now)
    {
        var validTotp = _twoFactorSecrets.Reveal(user) is { } secret && _totpService.VerifyCode(secret, code, now);
        if (!validTotp && !VerifyAndConsumeRecoveryCode(user, code, now)) return false;

        // Opportunistically upgrade legacy plaintext or old-key TOTP secrets to the active key.
        _twoFactorSecrets.TryRewrap(user, now);
        return true;
    }

    public async Task RequestPasswordResetAsync(
        string email,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var emailAddress = new EmailAddress(email);
        User? user = _unitOfWork.Users.SingleOrDefault(candidate => candidate.Email == emailAddress);
        if (user is not null)
        {
            var token = _tokenGenerator.GenerateUrlSafeToken();
            DateTimeOffset expiresAt = now.AddMinutes(Math.Max(5, _options.PasswordResetTokenMinutes));
            user.StartPasswordReset(TokenHashing.Sha256(token), expiresAt, now);

            await _auditWriter.StageAsync(new AuditEntry(null, user.Id, AuditActions.PasswordResetRequested,
                new AuditTarget("user", user.Id.ToString())) { CorrelationId = correlationId }, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            // IAM-10: deliver the token by email (link from the configured instance URL). The response stays the
            // same whether or not the account exists or mail is configured.
            _authEmails?.QueuePasswordReset(user, token, expiresAt);
        }
        else
        {
            // Unknown accounts are audited under a one-way subject so the log never stores the typed address.
            await _auditWriter.RecordAsync(new AuditEntry(null, null, AuditActions.PasswordResetRequested,
                AnonymousSubject(emailAddress.Value)) { CorrelationId = correlationId }, cancellationToken);
        }
    }

    public async Task<bool> ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        PasswordPolicy.Validate(newPassword);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var emailAddress = new EmailAddress(email);
        var tokenHash = TokenHashing.Sha256(token);
        User? user = _unitOfWork.Users.SingleOrDefault(candidate => candidate.Email == emailAddress);
        if (user?.PasswordResetTokenHash is null
            || user.PasswordResetTokenExpiresAt is null
            || user.PasswordResetTokenExpiresAt <= now
            || !FixedTimeEquals(user.PasswordResetTokenHash, tokenHash))
            return false;

        user.SetPasswordHash(_passwordHasher.HashPassword(newPassword), false, now);
        // IAM-09 (Coolify DeletesUserSessions): a reset ends every session and revokes every API token in the same
        // save, so a stolen session or token does not survive the recovery.
        var revokedSessions = _sessions is null
            ? 0
            : await _sessions.StageRevokeAllAsync(user.Id, null, UserSessionService.ReasonPasswordReset,
                correlationId, cancellationToken);
        PersonalAccessToken[] tokens = _unitOfWork.PersonalAccessTokens
            .Where(candidate => candidate.UserId == user.Id && candidate.RevokedAt == null)
            .ToArray();
        foreach (PersonalAccessToken accessToken in tokens) accessToken.Revoke(now);

        await _auditWriter.StageAsync(new AuditEntry(null, user.Id, AuditActions.PasswordResetCompleted,
            new AuditTarget("user", user.Id.ToString()))
        {
            CorrelationId = correlationId,
            Metadata = new Dictionary<string, object?>
            {
                ["revokedSessions"] = revokedSessions,
                ["revokedApiCredentials"] = tokens.Length
            }
        }, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<TwoFactorSetup> StartTwoFactorSetupAsync(
        UserId userId,
        string issuer,
        CancellationToken cancellationToken = default)
    {
        User user = await GetUserAsync(userId, cancellationToken);
        // Re-enrolling would silently replace (and, until confirmed, disable) an active second factor. Require the
        // re-confirmed disable flow first.
        if (user.TwoFactorConfirmedAt.HasValue)
            throw new InvalidOperationException(
                "Two-factor authentication is already enabled. Disable it before enrolling a new authenticator.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var secret = _totpService.GenerateSecret();
        user.StageTwoFactorSecret(_twoFactorSecrets.Protect(user.Id, secret), now);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new TwoFactorSetup(secret, _totpService.CreateOtpAuthUri(issuer, user.Email.Value, secret));
    }

    public Task<TwoFactorRecoveryCodes?> ConfirmTwoFactorAsync(
        UserId userId,
        string code,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        return ConfirmTwoFactorAsync(userId, code, null, correlationId, cancellationToken);
    }

    /// <summary>Confirms 2FA enrollment; the user's other sessions are revoked (IAM-09), the current one stays.</summary>
    public async Task<TwoFactorRecoveryCodes?> ConfirmTwoFactorAsync(
        UserId userId,
        string code,
        UserSessionId? currentSessionId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        User user = await GetUserAsync(userId, cancellationToken);
        string? secret = _twoFactorSecrets.Reveal(user);
        if (secret is null || !_totpService.VerifyCode(secret, code, DateTimeOffset.UtcNow))
            return null;

        IReadOnlyList<string> recoveryCodes = _totpService.GenerateRecoveryCodes();
        user.ConfirmTwoFactor(SerializeRecoveryCodeHashes(recoveryCodes), DateTimeOffset.UtcNow);
        await StageSecondFactorChangeAsync(user, currentSessionId, AuditActions.TwoFactorEnabled, correlationId,
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new TwoFactorRecoveryCodes(recoveryCodes);
    }

    /// <summary>
    ///     Disables two-factor authentication after re-confirming the user's identity with either a current TOTP code
    ///     or the account password. Returns <c>false</c> (and changes nothing) when the confirmation is missing or
    ///     invalid. Wrong passwords count towards the login lockout.
    /// </summary>
    public Task<bool> DisableTwoFactorAsync(
        UserId userId,
        string? currentPassword,
        string? twoFactorCode,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        return DisableTwoFactorAsync(userId, currentPassword, twoFactorCode, null, correlationId, cancellationToken);
    }

    /// <summary>Disables 2FA after re-confirmation; the user's other sessions are revoked (IAM-09).</summary>
    public async Task<bool> DisableTwoFactorAsync(
        UserId userId,
        string? currentPassword,
        string? twoFactorCode,
        UserSessionId? currentSessionId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        User user = await GetUserAsync(userId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (user.TwoFactorConfirmedAt.HasValue
            && !await ConfirmIdentityAsync(user, currentPassword, twoFactorCode, now, cancellationToken))
            return false;

        user.DisableTwoFactor(now);
        await StageSecondFactorChangeAsync(user, currentSessionId, AuditActions.TwoFactorDisabled, correlationId,
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task StageSecondFactorChangeAsync(User user, UserSessionId? currentSessionId, string action,
        string? correlationId, CancellationToken cancellationToken)
    {
        var revokedSessions = _sessions is null
            ? 0
            : await _sessions.StageRevokeAllAsync(user.Id, currentSessionId, UserSessionService.ReasonTwoFactorChanged,
                correlationId, cancellationToken);
        await _auditWriter.StageAsync(new AuditEntry(null, user.Id, action, new AuditTarget("user", user.Id.ToString()))
        {
            CorrelationId = correlationId,
            Metadata = new Dictionary<string, object?> { ["revokedSessions"] = revokedSessions }
        }, cancellationToken);
    }

    private async Task<bool> ConfirmIdentityAsync(
        User user,
        string? currentPassword,
        string? twoFactorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (user.IsLockedOut(now)) return false;

        if (!string.IsNullOrWhiteSpace(twoFactorCode)
            && _twoFactorSecrets.Reveal(user) is { } totpSecret
            && _totpService.VerifyCode(totpSecret, twoFactorCode, now))
            return true;

        if (string.IsNullOrEmpty(currentPassword) || !user.HasPassword) return false;
        if (_passwordHasher.VerifyPassword(user.PasswordHash!, currentPassword)) return true;

        user.RecordFailedLogin(
            Math.Max(1, _options.LockoutThreshold),
            TimeSpan.FromMinutes(Math.Max(1, _options.LockoutMinutes)),
            now);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return false;
    }

    private async Task<User> GetUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        return await _unitOfWork.UserRepository.GetByIdAsync(userId, cancellationToken)
               ?? throw new InvalidOperationException("User was not found.");
    }

    /// <summary>
    ///     Failed sign-ins are audited at warning level (IAM-08). The typed address is never stored: known accounts are
    ///     recorded under their user id, unknown ones under a one-way subject.
    /// </summary>
    private async Task AuditFailedLoginAsync(
        string email,
        string? correlationId,
        CancellationToken cancellationToken,
        UserId? userId = null,
        string reason = "invalid_credentials")
    {
        await _auditWriter.RecordAsync(new AuditEntry(null, null, AuditActions.UserLoginFailed,
            userId is { } id ? new AuditTarget("user", id.ToString()) : AnonymousSubject(email))
        {
            CorrelationId = correlationId,
            Level = AuditLevel.Warning,
            Metadata = new Dictionary<string, object?> { ["reason"] = reason }
        }, cancellationToken);
    }

    private async Task StageFailedLoginAsync(User user, string reason, string? correlationId,
        CancellationToken cancellationToken)
    {
        await _auditWriter.StageAsync(new AuditEntry(null, null, AuditActions.UserLoginFailed,
            new AuditTarget("user", user.Id.ToString()))
        {
            CorrelationId = correlationId,
            Level = AuditLevel.Warning,
            Metadata = new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["lockedOut"] = user.LockoutEndAt.HasValue
            }
        }, cancellationToken);
    }

    /// <summary>A stable, non-reversible audit subject for an address that has no account.</summary>
    internal static AuditTarget AnonymousSubject(string email)
    {
        return new AuditTarget("email_hash", TokenHashing.Sha256(email.Trim().ToLowerInvariant())[..32]);
    }

    private bool VerifyAndConsumeRecoveryCode(User user, string recoveryCode, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(user.TwoFactorRecoveryCodeHashes)) return false;

        var hash = TokenHashing.Sha256(recoveryCode);
        var hashes = user.TwoFactorRecoveryCodeHashes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!hashes.Any(candidate => FixedTimeEquals(candidate, hash))) return false;

        var updated = string.Join('\n', hashes.Where(candidate => !FixedTimeEquals(candidate, hash)));
        user.ReplaceTwoFactorRecoveryCodeHashes(updated, now);
        return true;
    }

    private static string SerializeRecoveryCodeHashes(IEnumerable<string> recoveryCodes)
    {
        return string.Join('\n', recoveryCodes.Select(TokenHashing.Sha256));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }
}
