using System.Globalization;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Settings;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Instance;

public static class InstanceSettingKeys
{
    /// <summary>Whether self-service registration is open. Default: closed once the first user exists.</summary>
    public const string RegistrationEnabled = "registration_enabled";
}

public sealed record InstanceSettingsSummary(bool RegistrationEnabled, int UserCount, int InstanceAdminCount);

public sealed record UpdateInstanceSettingsRequest(bool RegistrationEnabled);

public sealed record InstanceAdminSummary(UserId UserId, string Name, string Email);

/// <summary>Registration is closed by instance policy; the caller should answer 403.</summary>
public sealed class RegistrationClosedException()
    : InvalidOperationException("Registration is disabled on this instance. Ask an administrator for an invitation.");

/// <summary>
///     Instance administration (IAM-06): instance settings that belong to no team (self-service registration) and the
///     instance-administrator role. Instance administrators are independent of team roles; every operation here
///     requires <see cref="VesselPermissions.InstanceManage" />. The last instance administrator cannot be demoted.
/// </summary>
public sealed class InstanceSettingsService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>
    ///     True when a new account may self-register: always for the very first account (bootstrap; it becomes the
    ///     instance administrator), otherwise only when an instance administrator opened registration or the email
    ///     has a pending, unexpired team invitation.
    /// </summary>
    public bool CanRegister(string email)
    {
        if (!dbContext.Users.Any()) return true;
        if (IsRegistrationEnabled()) return true;

        var address = new EmailAddress(email);
        DateTimeOffset now = timeProvider.GetUtcNow();
        return dbContext.TeamInvitations.Any(invitation =>
            invitation.Email == address && invitation.AcceptedAt == null && invitation.ExpiresAt > now);
    }

    public bool IsRegistrationEnabled()
    {
        SettingEntry? setting = FindSystemSetting(InstanceSettingKeys.RegistrationEnabled);
        return setting is not null && bool.TryParse(setting.Value, out var enabled) && enabled;
    }

    public InstanceSettingsSummary Get(UserId actorUserId)
    {
        RequireInstanceManager(actorUserId);
        return new InstanceSettingsSummary(
            IsRegistrationEnabled(),
            dbContext.Users.Count(),
            dbContext.Users.Count(user => user.IsInstanceAdmin));
    }

    public async Task<InstanceSettingsSummary> UpdateAsync(
        UserId actorUserId,
        UpdateInstanceSettingsRequest request,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(actorUserId);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var value = request.RegistrationEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
        SettingEntry? setting = FindSystemSetting(InstanceSettingKeys.RegistrationEnabled);
        if (setting is null)
            await dbContext.SettingRepository.AddAsync(
                SettingEntry.Create(SettingScope.System, InstanceSettingKeys.RegistrationEnabled, value, now),
                cancellationToken);
        else
            setting.UpdateValue(value, now);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, AuditActions.InstanceSettingChanged,
            new AuditTarget("instance_setting", InstanceSettingKeys.RegistrationEnabled), correlationId,
            new Dictionary<string, object?> { ["value"] = request.RegistrationEnabled }, cancellationToken);

        return Get(actorUserId);
    }

    public IReadOnlyList<InstanceAdminSummary> ListAdmins(UserId actorUserId)
    {
        RequireInstanceManager(actorUserId);
        return dbContext.Users
            .Where(user => user.IsInstanceAdmin)
            .Select(user => new { user.Id, user.Name, user.Email })
            .AsEnumerable()
            .Select(user => new InstanceAdminSummary(user.Id, user.Name.Value, user.Email.Value))
            .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task GrantAdminAsync(
        UserId actorUserId,
        UserId targetUserId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(actorUserId);
        User target = await GetUserAsync(targetUserId, cancellationToken);
        target.GrantInstanceAdmin(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, AuditActions.InstanceAdminGranted,
            new AuditTarget("user", targetUserId.ToString()), correlationId, new Dictionary<string, object?>(),
            cancellationToken);
    }

    public async Task RevokeAdminAsync(
        UserId actorUserId,
        UserId targetUserId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(actorUserId);
        User target = await GetUserAsync(targetUserId, cancellationToken);
        if (!target.IsInstanceAdmin) return;
        if (dbContext.Users.Count(user => user.IsInstanceAdmin) <= 1)
            throw new InvalidOperationException("The last instance administrator cannot be demoted.");

        target.RevokeInstanceAdmin(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, AuditActions.InstanceAdminRevoked,
            new AuditTarget("user", targetUserId.ToString()), correlationId, new Dictionary<string, object?>(),
            cancellationToken);
    }

    private void RequireInstanceManager(UserId actorUserId)
    {
        if (!authorization.IsInstanceManager(actorUserId))
            throw new UnauthorizedAccessException("Instance administration requires an instance administrator.");
    }

    private SettingEntry? FindSystemSetting(string key)
    {
        return dbContext.Settings.FirstOrDefault(setting =>
            setting.Scope == SettingScope.System && setting.TeamId == null && setting.Key == key);
    }

    private async Task<User> GetUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        return await dbContext.UserRepository.GetByIdAsync(userId, cancellationToken)
               ?? throw new InvalidOperationException("User was not found.");
    }
}
