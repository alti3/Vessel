using System.Reflection;
using Vessel.Application.Auditing;
using Vessel.Application.Email;
using Vessel.Application.Notifications;
using Vessel.Domain.Notifications;

namespace Vessel.UnitTests.Application.Notifications;

/// <summary>NTF-03 failure classification/auto-disable and NTF-05 per-event routing in the dispatcher.</summary>
public sealed class NotificationDeliveryReliabilityTests
{
    [Fact]
    public async Task ConfigurationFailureIsNotRetriedAndCounts()
    {
        var provider = new ScriptedProvider(NotificationChannel.Webhook)
        {
            Default = NotificationDeliveryResult.FromHttpStatus("Webhook", 401, [401, 403, 404, 410])
        };
        var kit = NotificationTestKit.Create([provider]);
        var target = kit.AddTarget(NotificationChannel.Webhook);
        var notificationEvent = kit.AddEvent(NotificationEventTypes.DeploymentFailed);

        await kit.Dispatch.DispatchAsync(notificationEvent.Id.Value, TestContext.Current.CancellationToken);

        var attempt = Assert.Single(kit.Db.Attempts);
        Assert.Equal(NotificationDeliveryStatus.Failed, attempt.Status);
        Assert.Empty(kit.Jobs.Scheduled);
        Assert.Equal(1, target.ConsecutiveFailureCount);
        Assert.True(target.IsEnabled);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task TransientFailureIsRetriedAndDoesNotCount(int status)
    {
        var provider = new ScriptedProvider(NotificationChannel.Webhook)
        {
            Default = NotificationDeliveryResult.FromHttpStatus("Webhook", status, [401, 403, 404, 410])
        };
        var kit = NotificationTestKit.Create([provider]);
        var target = kit.AddTarget(NotificationChannel.Webhook);
        var notificationEvent = kit.AddEvent(NotificationEventTypes.DeploymentFailed);

        await kit.Dispatch.DispatchAsync(notificationEvent.Id.Value, TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryStatus.RetryScheduled, Assert.Single(kit.Db.Attempts).Status);
        Assert.Single(kit.Jobs.Scheduled);
        Assert.Equal(0, target.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task PayloadRejectionIsNeitherRetriedNorCounted()
    {
        var provider = new ScriptedProvider(NotificationChannel.Slack)
        {
            Default = NotificationDeliveryResult.FromHttpStatus("Slack", 400, [403, 404, 410])
        };
        var kit = NotificationTestKit.Create([provider]);
        var target = kit.AddTarget(NotificationChannel.Slack);

        await kit.Dispatch.DispatchAsync(kit.AddEvent(NotificationEventTypes.DeploymentFailed).Id.Value,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryStatus.Failed, Assert.Single(kit.Db.Attempts).Status);
        Assert.Empty(kit.Jobs.Scheduled);
        Assert.Equal(0, target.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task ThirdConfigurationFailureDisablesTargetAndTellsTheTeam()
    {
        var broken = new ScriptedProvider(NotificationChannel.Discord)
        {
            Default = NotificationDeliveryResult.FromHttpStatus("Discord", 404, [401, 403, 404])
        };
        var healthy = new ScriptedProvider(NotificationChannel.Slack);
        var kit = NotificationTestKit.Create([broken, healthy]);
        kit.ConfigureInstanceSmtp("alerts@example.com");
        var discord = kit.AddTarget(NotificationChannel.Discord, "Discord ops");
        kit.AddTarget(NotificationChannel.Slack, "Slack ops");

        for (var i = 0; i < NotificationTarget.AutoDisableThreshold; i++)
            await kit.Dispatch.DispatchAsync(kit.AddEvent(NotificationEventTypes.DeploymentFailed).Id.Value,
                TestContext.Current.CancellationToken);

        Assert.False(discord.IsEnabled);
        Assert.Contains("HTTP 404", discord.DisabledReason, StringComparison.Ordinal);

        // One channel-disabled event: in-app notice, dispatch queued to the remaining channels.
        var disabledEvent = Assert.Single(kit.Db.Events, item => item.EventType == NotificationEventTypes.ChannelDisabled);
        Assert.Contains(kit.Db.InApp, item => item.EventId == disabledEvent.Id);
        Assert.Contains(kit.Audit.Records, record => record.Action == AuditActions.NotificationTargetAutoDisabled);

        // Team admins (the owner) are emailed through the instance settings with the notification sender.
        var email = Assert.Single(kit.QueuedEmails());
        Assert.Equal(["owner@example.com"], email.To);
        Assert.Contains("Discord ops", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://vessel.example.com/notifications", email.TextBody, StringComparison.Ordinal);
        var job = Assert.Single(kit.Jobs.Enqueued, item => item.JobType == typeof(SendInstanceEmailJob));
        Assert.Equal(EmailPurpose.Notification, job.Arguments[1]);

        // The disabled target is no longer selected; the healthy one receives the notice.
        await kit.Dispatch.DispatchAsync(disabledEvent.Id.Value, TestContext.Current.CancellationToken);
        Assert.Equal(NotificationTarget.AutoDisableThreshold, broken.Calls.Count);
        Assert.Contains(healthy.Calls, call => call.Message.EventType == NotificationEventTypes.ChannelDisabled);
    }

    [Fact]
    public async Task TestSendsNeverCountTowardsAutoDisable()
    {
        var provider = new ScriptedProvider(NotificationChannel.Webhook)
        {
            Default = NotificationDeliveryResult.ConfigurationError("Webhook returned HTTP 410.")
        };
        var kit = NotificationTestKit.Create([provider]);
        var target = kit.AddTarget(NotificationChannel.Webhook);

        for (var i = 0; i < 5; i++)
        {
            var test = NotificationEvent.Create(kit.Db.TeamId, null, NotificationEventTypes.Test,
                NotificationSeverity.Info, NotificationTargetType.System, target.Id.Value.ToString("D"), "Test",
                "Test.", "{}", null, kit.Time.GetUtcNow());
            kit.Db.Events.Add(test);
            await kit.Dispatch.DispatchAsync(test.Id.Value, TestContext.Current.CancellationToken);
        }

        Assert.Equal(5, provider.Calls.Count);
        Assert.True(target.IsEnabled);
        Assert.Equal(0, target.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task RoutingFollowsPerTargetSubscriptions()
    {
        var provider = new ScriptedProvider(NotificationChannel.Slack);
        var kit = NotificationTestKit.Create([provider]);
        var defaults = kit.AddTarget(NotificationChannel.Slack, "Defaults");
        var successesOnly = kit.AddTarget(NotificationChannel.Slack, "Successes");
        successesOnly.SetSubscriptions([NotificationEventTypes.DeploymentSucceeded], kit.Time.GetUtcNow());

        await kit.Dispatch.DispatchAsync(kit.AddEvent(NotificationEventTypes.DeploymentSucceeded,
            NotificationSeverity.Info).Id.Value, TestContext.Current.CancellationToken);
        await kit.Dispatch.DispatchAsync(kit.AddEvent(NotificationEventTypes.DeploymentFailed).Id.Value,
            TestContext.Current.CancellationToken);

        Assert.Equal([successesOnly.Id.Value], provider.Calls
            .Where(call => call.Message.EventType == NotificationEventTypes.DeploymentSucceeded)
            .Select(call => call.Target.TargetId));
        Assert.Equal([defaults.Id.Value], provider.Calls
            .Where(call => call.Message.EventType == NotificationEventTypes.DeploymentFailed)
            .Select(call => call.Target.TargetId));
    }

    [Fact]
    public async Task AlwaysDeliveredEventsIgnoreSubscriptionsAndSeverityButUnknownEventsAreDropped()
    {
        var provider = new ScriptedProvider(NotificationChannel.Slack);
        var kit = NotificationTestKit.Create([provider]);
        var target = kit.AddTarget(NotificationChannel.Slack, minimumSeverity: NotificationSeverity.Critical);
        target.SetSubscriptions([], kit.Time.GetUtcNow());

        await kit.Dispatch.DispatchAsync(kit.AddEvent(NotificationEventTypes.CertificateRenewalFailed).Id.Value,
            TestContext.Current.CancellationToken);
        await kit.Dispatch.DispatchAsync(kit.AddEvent(NotificationEventTypes.DeploymentFailed,
            NotificationSeverity.Critical).Id.Value, TestContext.Current.CancellationToken);
        await kit.Dispatch.DispatchAsync(kit.AddEvent("custom.unknown", NotificationSeverity.Critical).Id.Value,
            TestContext.Current.CancellationToken);

        var call = Assert.Single(provider.Calls);
        Assert.Equal(NotificationEventTypes.CertificateRenewalFailed, call.Message.EventType);
    }

    [Fact]
    public async Task DispatchPassesPerEventRouteAndAbsoluteInstanceLinks()
    {
        var provider = new ScriptedProvider(NotificationChannel.Telegram);
        var kit = NotificationTestKit.Create([provider], publicUrl: "https://vessel.example.com/base");
        var target = kit.AddTarget(NotificationChannel.Telegram);
        target.SetEventRoutes(new Dictionary<string, string> { [NotificationEventTypes.DeploymentFailed] = "77" },
            kit.Time.GetUtcNow());

        await kit.Dispatch.DispatchAsync(kit.AddEvent(NotificationEventTypes.DeploymentFailed,
            resourceUrl: "/deployments/abc").Id.Value, TestContext.Current.CancellationToken);

        var call = Assert.Single(provider.Calls);
        Assert.Equal("77", call.Target.EventRoute);
        Assert.Equal("https://vessel.example.com/base/deployments/abc", call.Message.ResourceUrl);
    }

    [Fact]
    public void EveryProducerEventTypeIsInTheCatalog()
    {
        var constants = typeof(NotificationEventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(constants);
        Assert.All(constants, key => Assert.True(NotificationEventCatalog.IsKnown(key), key));
    }

    [Fact]
    public void HttpStatusClassificationMatchesUpstream()
    {
        int[] configuration = [401, 403, 404, 410];
        Assert.Equal(NotificationFailureKind.Configuration,
            NotificationDeliveryResult.FromHttpStatus("Webhook", 410, configuration).FailureKind);
        Assert.Equal(NotificationFailureKind.Transient,
            NotificationDeliveryResult.FromHttpStatus("Webhook", 429, configuration).FailureKind);
        Assert.Equal(NotificationFailureKind.Transient,
            NotificationDeliveryResult.FromHttpStatus("Webhook", 502, configuration).FailureKind);
        Assert.Equal(NotificationFailureKind.Payload,
            NotificationDeliveryResult.FromHttpStatus("Webhook", 422, configuration).FailureKind);
        Assert.DoesNotContain("http", NotificationDeliveryResult.FromHttpStatus("Webhook", 404, configuration)
            .FailureReason!.Replace("HTTP", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
