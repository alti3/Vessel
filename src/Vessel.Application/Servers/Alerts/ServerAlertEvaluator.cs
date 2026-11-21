using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Notifications;
using Vessel.Domain.Servers;

namespace Vessel.Application.Servers.Alerts;

/// <summary>
///     Server alert producers that run after each metrics sample (SRV-08): the disk-usage alert fires at or above the
///     server's threshold at most once per its interval (default 24 h) and re-arms early once usage drops
///     <see cref="ServerMonitoringSettings.DiskAlertRecoveryMargin" /> points below the threshold (upstream
///     <c>HighDiskUsage::hasRecovered</c>). The producer state on <see cref="ServerMonitoringSettings" /> owns the
///     per-server interval; the catalog's daily dispatch throttle stays as the guard against duplicate producers and is
///     released only when this producer decided an alert is due (a shorter per-server interval or a re-armed alert).
///     The reachability alerts live in the health poll (<c>ServerHealthPollingService</c>), which notifies after
///     <see cref="Server.UnreachableAlertThreshold" /> consecutive failed checks.
/// </summary>
public sealed class ServerAlertEvaluator(
    INotificationEventPublisher notifications,
    INotificationThrottle throttle,
    TimeProvider timeProvider)
{
    /// <summary>Applies the disk alert rule. The caller saves <paramref name="settings" />.</summary>
    public async Task<DiskAlertDecision> EvaluateDiskAsync(Server server, ServerMonitoringSettings settings,
        decimal? usagePercent, string? mountPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);
        if (usagePercent is not { } usage) return DiskAlertDecision.None;

        DiskAlertDecision decision = settings.EvaluateDiskUsage(usage, timeProvider.GetUtcNow());
        if (decision == DiskAlertDecision.Alert)
        {
            await ReleaseDispatchThrottleAsync(server, cancellationToken);
            await notifications.PublishAsync(server.TeamId,
                MonitoringNotifications.DiskUsageHigh(server.Id.Value, server.Name.Value, usage,
                    settings.DiskAlertThresholdPercent, mountPoint), CancellationToken.None);
        }

        return decision;
    }

    private async Task ReleaseDispatchThrottleAsync(Server server, CancellationToken cancellationToken)
    {
        var subject = NotificationThrottle.SubjectKeyFor(NotificationTargetType.Server, server.Id.Value.ToString("D"));
        if (subject is null) return;
        try
        {
            await throttle.ReleaseAsync(server.TeamId, NotificationEventTypes.ServerDiskUsageHigh, subject,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort: at worst the daily dispatch throttle suppresses this alert.
        }
    }
}
