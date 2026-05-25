using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Users;

public sealed class User : Entity<UserId>
{
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

    public string? ExternalSubject { get; private set; }

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

    public void StageTwoFactorSecret(string secret, DateTimeOffset now)
    {
        TwoFactorSecret = DomainValidation.Required(secret, nameof(TwoFactorSecret), 128);
        TwoFactorConfirmedAt = null;
        TwoFactorRecoveryCodeHashes = null;
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
