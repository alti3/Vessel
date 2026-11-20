using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;

namespace Vessel.UnitTests.Application.Notifications;

/// <summary>NTF-04: duplicate suppression keyed by team + event type + resource.</summary>
public sealed class NotificationThrottlingTests
{
    private static readonly Guid ServerId = Guid.Parse("7f0c7d4e-9d61-4b55-9f39-1a4e6f1f2c11");

    [Fact]
    public async Task IntervalThrottleSuppressesRepeatsUntilTheWindowPasses()
    {
        var kit = NotificationTestKit.Create();

        Assert.NotNull(await Raise(kit, NotificationEventTypes.ServerDiskUsageHigh, ServerId));
        kit.Time.Advance(TimeSpan.FromHours(23));
        Assert.Null(await Raise(kit, NotificationEventTypes.ServerDiskUsageHigh, ServerId));
        // A different server is a different subject.
        Assert.NotNull(await Raise(kit, NotificationEventTypes.ServerDiskUsageHigh, Guid.NewGuid()));
        kit.Time.Advance(TimeSpan.FromHours(2));
        Assert.NotNull(await Raise(kit, NotificationEventTypes.ServerDiskUsageHigh, ServerId));

        Assert.Equal(3, kit.Db.Events.Count);
        Assert.Equal(3, kit.Db.InApp.Count);
    }

    [Fact]
    public async Task RestartLoopsAlertAtMostOncePerHour()
    {
        var kit = NotificationTestKit.Create();
        var applicationId = Guid.NewGuid();

        for (var i = 0; i < 12; i++)
        {
            await Raise(kit, NotificationEventTypes.ApplicationRestartLimitReached, applicationId,
                NotificationTargetType.Application);
            kit.Time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Single(kit.Db.Events);
        kit.Time.Advance(TimeSpan.FromMinutes(1));
        await Raise(kit, NotificationEventTypes.ApplicationRestartLimitReached, applicationId,
            NotificationTargetType.Application);
        Assert.Equal(2, kit.Db.Events.Count);
    }

    [Fact]
    public async Task FlappingServerAlertsOncePerIncidentAndRecoveryOnlyAfterAnAlert()
    {
        var kit = NotificationTestKit.Create();

        // Recovery without an alerted incident is suppressed.
        Assert.Null(await Raise(kit, NotificationEventTypes.ServerReachable, ServerId));

        Assert.NotNull(await Raise(kit, NotificationEventTypes.ServerUnreachable, ServerId));
        kit.Time.Advance(TimeSpan.FromDays(3));
        Assert.Null(await Raise(kit, NotificationEventTypes.ServerUnreachable, ServerId));

        Assert.NotNull(await Raise(kit, NotificationEventTypes.ServerReachable, ServerId));
        Assert.Null(await Raise(kit, NotificationEventTypes.ServerReachable, ServerId));

        // A new incident after recovery alerts again.
        Assert.NotNull(await Raise(kit, NotificationEventTypes.ServerUnreachable, ServerId));

        Assert.Equal([NotificationEventTypes.ServerUnreachable, NotificationEventTypes.ServerReachable,
            NotificationEventTypes.ServerUnreachable], kit.Db.Events.Select(item => item.EventType));
    }

    [Fact]
    public async Task BackupSuccessReleasesTheDailyFailureThrottle()
    {
        var kit = NotificationTestKit.Create();
        var databaseId = Guid.NewGuid();

        Assert.NotNull(await Raise(kit, NotificationEventTypes.BackupFailed, databaseId, NotificationTargetType.Database));
        Assert.Null(await Raise(kit, NotificationEventTypes.BackupFailed, databaseId, NotificationTargetType.Database));
        Assert.NotNull(await Raise(kit, NotificationEventTypes.BackupSucceeded, databaseId,
            NotificationTargetType.Database));
        Assert.NotNull(await Raise(kit, NotificationEventTypes.BackupFailed, databaseId, NotificationTargetType.Database));
    }

    [Fact]
    public async Task UnthrottledEventsAreAlwaysRaised()
    {
        var kit = NotificationTestKit.Create();
        var deploymentId = Guid.NewGuid();

        Assert.NotNull(await Raise(kit, NotificationEventTypes.DeploymentFailed, deploymentId,
            NotificationTargetType.Deployment));
        Assert.NotNull(await Raise(kit, NotificationEventTypes.DeploymentFailed, deploymentId,
            NotificationTargetType.Deployment));
        Assert.Empty(kit.Throttle.Claims);
    }

    [Fact]
    public async Task ClaimIsReleasedWhenTheEventCannotBePersisted()
    {
        var kit = NotificationTestKit.Create();
        kit.Db.FailNextSave = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Raise(kit, NotificationEventTypes.ServerDiskUsageHigh, ServerId));

        Assert.Empty(kit.Throttle.Claims);
        Assert.NotNull(await Raise(kit, NotificationEventTypes.ServerDiskUsageHigh, ServerId));
    }

    [Fact]
    public async Task PurgeJobRemovesOldClaims()
    {
        var kit = NotificationTestKit.Create();
        await Raise(kit, NotificationEventTypes.ServerUnreachable, ServerId);
        kit.Time.Advance(NotificationThrottlePurgeJob.Retention + TimeSpan.FromDays(1));

        Assert.Equal(1, await new NotificationThrottlePurgeJob(kit.Throttle, kit.Time)
            .RunAsync(TestContext.Current.CancellationToken));
        Assert.Empty(kit.Throttle.Claims);
    }

    private static Task<Guid?> Raise(NotificationTestKit kit, string eventType, Guid resourceId,
        NotificationTargetType targetType = NotificationTargetType.Server)
    {
        return kit.Events.RaiseAsync(kit.Db.TeamId,
            new RaiseNotificationRequest(eventType, NotificationSeverity.Error, targetType, resourceId.ToString("D"),
                "Title", "Message.", "{}", null, null), TestContext.Current.CancellationToken);
    }
}
