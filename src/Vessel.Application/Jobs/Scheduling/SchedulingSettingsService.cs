using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Settings;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Jobs.Scheduling;

/// <summary>Instance-wide scheduling defaults: time zone and catch-up policy (NTF-08). Instance administrators only.</summary>
public sealed class SchedulingSettingsService(
    IVesselDbContext dbContext,
    SystemSettingsStore settings,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter)
{
    public const string SettingKey = "scheduling.defaults";

    /// <summary>The effective defaults (no authorization: used by the dispatcher).</summary>
    public SchedulingSettings Current()
    {
        SchedulingSettings value = settings.Get(SettingKey, new SchedulingSettings());
        return CronSchedule.TryResolveTimeZone(value.TimeZone, out _) ? value : value with { TimeZone = "UTC" };
    }

    public SchedulingSettings Get(UserId actorUserId)
    {
        RequireInstanceManager(actorUserId);
        return Current();
    }

    public async Task<SchedulingSettings> UpdateAsync(UserId actorUserId, UpdateSchedulingSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireInstanceManager(actorUserId);
        SchedulingSettings current = Current();
        var zone = string.IsNullOrWhiteSpace(request.TimeZone) ? current.TimeZone : request.TimeZone.Trim();
        if (!CronSchedule.TryResolveTimeZone(zone, out _)) throw new DomainException($"Unknown time zone '{zone}'.");
        var updated = new SchedulingSettings(zone, request.MissedRunPolicy ?? current.MissedRunPolicy);
        await settings.SetAsync(SettingKey, updated, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(null, actorUserId, AuditActions.InstanceSettingChanged,
            new AuditTarget("instance_setting", SettingKey), null,
            new Dictionary<string, object?>
            {
                ["timeZone"] = updated.TimeZone,
                ["missedRunPolicy"] = updated.MissedRunPolicy.ToString()
            }, cancellationToken);
        return updated;
    }

    private void RequireInstanceManager(UserId actorUserId)
    {
        if (!authorization.IsInstanceManager(actorUserId))
            throw new UnauthorizedAccessException("Scheduling settings require an instance administrator.");
    }
}
