using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
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
    private readonly AuthOptions _options;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly TotpService _totpService;
    private readonly IVesselDbContext _unitOfWork;

    public VesselAuthenticationService(
        IVesselDbContext unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenGenerator tokenGenerator,
        TotpService totpService,
        IAuditWriter auditWriter,
        IOptions<AuthOptions> options)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _totpService = totpService;
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
        if (_unitOfWork.Users.Any(user => user.Email == emailAddress))
            throw new InvalidOperationException("A user with this email already exists.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        User user = User.Create(new DisplayName(name), emailAddress, now);
        user.SetPasswordHash(_passwordHasher.HashPassword(password), forcePasswordReset: false, now);
        Team team = Team.Create(new DisplayName($"{user.Name.Value}'s Team"), user.Id, isPersonal: true, now);

        await _unitOfWork.UserRepository.AddAsync(user, cancellationToken);
        await _unitOfWork.TeamRepository.AddAsync(team, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            team.Id,
            user.Id,
            AuditActions.UserRegistered,
            new AuditTarget("user", user.Id.ToString()),
            correlationId,
            new Dictionary<string, object?> { ["email"] = user.Email.Value },
            cancellationToken);

        return new AuthenticatedUser(user.Id, team.Id, user.Name.Value, user.Email.Value, TwoFactorRequired: false);
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
            await AuditFailedLoginAsync(emailAddress.Value, correlationId, cancellationToken);
            return null;
        }

        if (user.IsLockedOut(now))
        {
            await AuditFailedLoginAsync(emailAddress.Value, correlationId, cancellationToken);
            return null;
        }

        if (!_passwordHasher.VerifyPassword(user.PasswordHash!, password))
        {
            user.RecordFailedLogin(
                Math.Max(1, _options.LockoutThreshold),
                TimeSpan.FromMinutes(Math.Max(1, _options.LockoutMinutes)),
                now);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await AuditFailedLoginAsync(emailAddress.Value, correlationId, cancellationToken);
            return null;
        }

        if (user.TwoFactorConfirmedAt.HasValue)
        {
            if (string.IsNullOrWhiteSpace(twoFactorCode))
            {
                return new AuthenticatedUser(user.Id, default, user.Name.Value, user.Email.Value, TwoFactorRequired: true);
            }

            bool validTotp = _totpService.VerifyCode(user.TwoFactorSecret!, twoFactorCode, now);
            bool validRecoveryCode = validTotp || VerifyAndConsumeRecoveryCode(user, twoFactorCode, now);
            if (!validRecoveryCode)
            {
                await AuditFailedLoginAsync(emailAddress.Value, correlationId, cancellationToken);
                return null;
            }
        }

        Team team = ResolveLoginTeam(user);
        user.RecordSuccessfulLogin(now);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            team.Id,
            user.Id,
            AuditActions.UserLoggedIn,
            new AuditTarget("user", user.Id.ToString()),
            correlationId,
            new Dictionary<string, object?> { ["email"] = user.Email.Value },
            cancellationToken);

        return new AuthenticatedUser(user.Id, team.Id, user.Name.Value, user.Email.Value, TwoFactorRequired: false);
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
            string token = _tokenGenerator.GenerateUrlSafeToken();
            user.StartPasswordReset(
                TokenHashing.Sha256(token),
                now.AddMinutes(Math.Max(5, _options.PasswordResetTokenMinutes)),
                now);

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await _auditWriter.RecordAsync(
            null,
            user?.Id,
            AuditActions.PasswordResetRequested,
            new AuditTarget("user", user?.Id.ToString() ?? emailAddress.Value),
            correlationId,
            new Dictionary<string, object?> { ["email"] = emailAddress.Value },
            cancellationToken);
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
        string tokenHash = TokenHashing.Sha256(token);
        User? user = _unitOfWork.Users.SingleOrDefault(candidate => candidate.Email == emailAddress);
        if (user?.PasswordResetTokenHash is null
            || user.PasswordResetTokenExpiresAt is null
            || user.PasswordResetTokenExpiresAt <= now
            || !FixedTimeEquals(user.PasswordResetTokenHash, tokenHash))
            return false;

        user.SetPasswordHash(_passwordHasher.HashPassword(newPassword), forcePasswordReset: false, now);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            null,
            user.Id,
            AuditActions.PasswordResetCompleted,
            new AuditTarget("user", user.Id.ToString()),
            correlationId,
            new Dictionary<string, object?>(),
            cancellationToken);

        return true;
    }

    public async Task<TwoFactorSetup> StartTwoFactorSetupAsync(
        UserId userId,
        string issuer,
        CancellationToken cancellationToken = default)
    {
        User user = await GetUserAsync(userId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string secret = _totpService.GenerateSecret();
        user.StageTwoFactorSecret(secret, now);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new TwoFactorSetup(secret, _totpService.CreateOtpAuthUri(issuer, user.Email.Value, secret));
    }

    public async Task<TwoFactorRecoveryCodes?> ConfirmTwoFactorAsync(
        UserId userId,
        string code,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        User user = await GetUserAsync(userId, cancellationToken);
        if (user.TwoFactorSecret is null || !_totpService.VerifyCode(user.TwoFactorSecret, code, DateTimeOffset.UtcNow))
            return null;

        IReadOnlyList<string> recoveryCodes = _totpService.GenerateRecoveryCodes();
        user.ConfirmTwoFactor(SerializeRecoveryCodeHashes(recoveryCodes), DateTimeOffset.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            null,
            user.Id,
            AuditActions.TwoFactorEnabled,
            new AuditTarget("user", user.Id.ToString()),
            correlationId,
            new Dictionary<string, object?>(),
            cancellationToken);

        return new TwoFactorRecoveryCodes(recoveryCodes);
    }

    public async Task DisableTwoFactorAsync(
        UserId userId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        User user = await GetUserAsync(userId, cancellationToken);
        user.DisableTwoFactor(DateTimeOffset.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _auditWriter.RecordAsync(
            null,
            user.Id,
            AuditActions.TwoFactorDisabled,
            new AuditTarget("user", user.Id.ToString()),
            correlationId,
            new Dictionary<string, object?>(),
            cancellationToken);
    }

    private Team ResolveLoginTeam(User user)
    {
        TeamId? invitationTeamId = _unitOfWork.TeamInvitations
            .Where(invitation => invitation.Email == user.Email && !invitation.AcceptedAt.HasValue)
            .OrderBy(invitation => invitation.CreatedAt)
            .Select(invitation => (TeamId?)invitation.TeamId)
            .FirstOrDefault();

        Team? invitedTeam = invitationTeamId.HasValue
            ? _unitOfWork.Teams.SingleOrDefault(team => team.Id == invitationTeamId.Value)
            : null;

        if (invitedTeam is not null) return invitedTeam;

        Team? personalTeam = _unitOfWork.Teams
            .Where(team => team.IsPersonal)
            .SingleOrDefault(team => team.Memberships.Any(member => member.UserId == user.Id));

        if (personalTeam is not null) return personalTeam;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Team team = Team.Create(new DisplayName($"{user.Name.Value}'s Team"), user.Id, isPersonal: true, now);
        _unitOfWork.TeamRepository.AddAsync(team).GetAwaiter().GetResult();
        return team;
    }

    private async Task<User> GetUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        return await _unitOfWork.UserRepository.GetByIdAsync(userId, cancellationToken)
               ?? throw new InvalidOperationException("User was not found.");
    }

    private async Task AuditFailedLoginAsync(
        string email,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        await _auditWriter.RecordAsync(
            null,
            null,
            AuditActions.UserLoginFailed,
            new AuditTarget("user", email),
            correlationId,
            new Dictionary<string, object?> { ["email"] = email },
            cancellationToken);
    }

    private bool VerifyAndConsumeRecoveryCode(User user, string recoveryCode, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(user.TwoFactorRecoveryCodeHashes)) return false;

        string hash = TokenHashing.Sha256(recoveryCode);
        string[] hashes = user.TwoFactorRecoveryCodeHashes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!hashes.Any(candidate => FixedTimeEquals(candidate, hash))) return false;

        string updated = string.Join('\n', hashes.Where(candidate => !FixedTimeEquals(candidate, hash)));
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
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));
    }
}
