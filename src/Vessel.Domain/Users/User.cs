using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Users;

public sealed class User : Entity<UserId>
{
    public const int TwoFactorSecretMaxLength = 512;

    private User()
    {
    }

    private User(UserId id, DisplayName name, EmailAddress email, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Name = name;
        Email = email;
    }

    public DisplayName Name { get; private set; }

    public EmailAddress Email { get; private set; }

    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    /// <summary>
    ///     Deprecated (IAM-07): a single subject cannot identify the provider or issuer. External identities live in
    ///     <c>OauthIdentity</c>; this column is kept only so existing rows still load.
    /// </summary>
    public string? ExternalSubject { get; private set; }

    /// <summary>
    ///     The team the user last worked in (IAM-09). Login restores it when the user is still a member; otherwise
    ///     the personal team is used. It is a preference, not an authorization input.
    /// </summary>
    public TeamId? CurrentTeamId { get; private set; }

    public string? PasswordHash { get; private set; }

    public bool ForcePasswordReset { get; private set; }

    public bool MarketingEmailsEnabled { get; private set; }

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockoutEndAt { get; private set; }

    public string? PasswordResetTokenHash { get; private set; }

    public DateTimeOffset? PasswordResetTokenExpiresAt { get; private set; }

    public string? TwoFactorSecret { get; private set; }

    public DateTimeOffset? TwoFactorConfirmedAt { get; private set; }

    public string? TwoFactorRecoveryCodeHashes { get; private set; }

    /// <summary>
    /// Instance administrators may use host-local execution (the control-plane host's Docker/Podman
    /// runtime, host terminals) and run instance-wide maintenance such as secret re-encryption.
    /// This is an instance-level privilege and is independent of team roles.
    /// </summary>
    public bool IsInstanceAdmin { get; private set; }

    public bool HasPassword => !string.IsNullOrWhiteSpace(PasswordHash);

    public static User Create(DisplayName name, EmailAddress email, DateTimeOffset now)
    {
        return new User(UserId.New(), name, email, now);
    }

    public void VerifyEmail(DateTimeOffset now)
    {
        EmailVerifiedAt = now;
        Touch(now);
    }

    /// <summary>
    ///     Remembers the active team for the next login. It deliberately does not rotate the concurrency stamp, so a
    ///     team switch never conflicts with another switch (last writer wins).
    /// </summary>
    public void SetCurrentTeam(TeamId teamId)
    {
        CurrentTeamId = teamId;
    }

    /// <summary>Forgets the stored team when it is the given one (for example after the member was removed).</summary>
    public void ClearCurrentTeam(TeamId teamId)
    {
        if (CurrentTeamId == teamId) CurrentTeamId = null;
    }

    [Obsolete("Use OauthIdentity (IAM-07); ExternalSubject cannot hold the provider or issuer.")]
    public void LinkExternalSubject(string externalSubject, DateTimeOffset now)
    {
        ExternalSubject = DomainValidation.Required(externalSubject, nameof(ExternalSubject), 255);
        Touch(now);
    }

    public void SetPasswordHash(string passwordHash, bool forcePasswordReset, DateTimeOffset now)
    {
        PasswordHash = DomainValidation.Required(passwordHash, nameof(PasswordHash), 512);
        ForcePasswordReset = forcePasswordReset;
        PasswordResetTokenHash = null;
        PasswordResetTokenExpiresAt = null;
        FailedLoginCount = 0;
        LockoutEndAt = null;
        Touch(now);
    }

    public bool IsLockedOut(DateTimeOffset now)
    {
        return LockoutEndAt.HasValue && LockoutEndAt.Value > now;
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockoutEndAt = null;
        Touch(now);
    }

    public void RecordFailedLogin(int lockoutThreshold, TimeSpan lockoutDuration, DateTimeOffset now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= lockoutThreshold)
        {
            LockoutEndAt = now.Add(lockoutDuration);
            FailedLoginCount = 0;
        }

        Touch(now);
    }

    public void RequirePasswordReset(DateTimeOffset now)
    {
        ForcePasswordReset = true;
        Touch(now);
    }

    public void ClearForcePasswordReset(DateTimeOffset now)
    {
        ForcePasswordReset = false;
        Touch(now);
    }

    public void StartPasswordReset(string tokenHash, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        PasswordResetTokenHash = DomainValidation.Required(tokenHash, nameof(PasswordResetTokenHash), 128);
        PasswordResetTokenExpiresAt = expiresAt;
        Touch(now);
    }

    public void ClearPasswordReset(DateTimeOffset now)
    {
        PasswordResetTokenHash = null;
        PasswordResetTokenExpiresAt = null;
        Touch(now);
    }

    public void GrantInstanceAdmin(DateTimeOffset now)
    {
        if (IsInstanceAdmin) return;
        IsInstanceAdmin = true;
        Touch(now);
    }

    public void RevokeInstanceAdmin(DateTimeOffset now)
    {
        if (!IsInstanceAdmin) return;
        IsInstanceAdmin = false;
        Touch(now);
    }

    public void StageTwoFactorSecret(string protectedSecret, DateTimeOffset now)
    {
        TwoFactorSecret = DomainValidation.Required(protectedSecret, nameof(TwoFactorSecret), TwoFactorSecretMaxLength);
        TwoFactorConfirmedAt = null;
        TwoFactorRecoveryCodeHashes = null;
        Touch(now);
    }

    /// <summary>
    /// Replaces the stored (protected) TOTP secret without changing the 2FA enrollment state.
    /// Used when the at-rest protection is upgraded or re-encrypted with a new key.
    /// </summary>
    public void RewrapTwoFactorSecret(string protectedSecret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(TwoFactorSecret))
            throw new DomainException("Two-factor authentication has not been started.");

        TwoFactorSecret = DomainValidation.Required(protectedSecret, nameof(TwoFactorSecret), TwoFactorSecretMaxLength);
        Touch(now);
    }

    public void ConfirmTwoFactor(string recoveryCodeHashesJson, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(TwoFactorSecret))
            throw new DomainException("Two-factor authentication has not been started.");

        TwoFactorRecoveryCodeHashes = DomainValidation.Required(
            recoveryCodeHashesJson,
            nameof(TwoFactorRecoveryCodeHashes),
            4000);
        TwoFactorConfirmedAt = now;
        Touch(now);
    }

    public void ReplaceTwoFactorRecoveryCodeHashes(string recoveryCodeHashesJson, DateTimeOffset now)
    {
        if (TwoFactorConfirmedAt is null)
            throw new DomainException("Two-factor authentication is not enabled.");

        TwoFactorRecoveryCodeHashes = DomainValidation.Optional(
            recoveryCodeHashesJson,
            nameof(TwoFactorRecoveryCodeHashes),
            4000);
        Touch(now);
    }

    public void DisableTwoFactor(DateTimeOffset now)
    {
        TwoFactorSecret = null;
        TwoFactorConfirmedAt = null;
        TwoFactorRecoveryCodeHashes = null;
        Touch(now);
    }
}
