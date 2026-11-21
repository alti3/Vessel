using System.Globalization;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

/// <summary>NTF-03 (failure tracking), NTF-05 (event catalog and subscriptions), NTF-06 (instance email settings).</summary>
public sealed class NotificationTargetReliabilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void ThreeConfigurationFailuresDisableTheTargetExactlyOnce()
    {
        var target = Target(NotificationChannel.Slack);

        Assert.False(target.RecordDeliveryFailure(NotificationFailureKind.Configuration, "Slack returned HTTP 404.", Now));
        Assert.False(target.RecordDeliveryFailure(NotificationFailureKind.Configuration, "Slack returned HTTP 404.", Now));
        Assert.True(target.IsEnabled);
        Assert.True(target.RecordDeliveryFailure(NotificationFailureKind.Configuration, "Slack returned HTTP 404.", Now));

        Assert.False(target.IsEnabled);
        Assert.Equal("Slack returned HTTP 404.", target.DisabledReason);
        Assert.Equal(Now, target.AutoDisabledAt);
        Assert.False(target.RecordDeliveryFailure(NotificationFailureKind.Configuration, "again", Now));
    }

    [Theory]
    [InlineData(NotificationFailureKind.Transient)]
    [InlineData(NotificationFailureKind.Payload)]
    public void TransientAndPayloadFailuresNeverCount(NotificationFailureKind kind)
    {
        var target = Target(NotificationChannel.Webhook);

        for (var i = 0; i < 10; i++)
            Assert.False(target.RecordDeliveryFailure(kind, "HTTP 503", Now));

        Assert.True(target.IsEnabled);
        Assert.Equal(0, target.ConsecutiveFailureCount);
    }

    [Fact]
    public void SuccessAndSettingsChangesResetTheCounter()
    {
        var target = Target(NotificationChannel.Discord);
        target.RecordDeliveryFailure(NotificationFailureKind.Configuration, "401", Now);
        target.RecordDeliveryFailure(NotificationFailureKind.Configuration, "401", Now);
        target.RecordDeliverySuccess(Now);
        Assert.Equal(0, target.ConsecutiveFailureCount);

        for (var i = 0; i < NotificationTarget.AutoDisableThreshold; i++)
            target.RecordDeliveryFailure(NotificationFailureKind.Configuration, "401", Now);
        Assert.False(target.IsEnabled);

        target.Configure(target.Name, target.CredentialsReferenceId, "{}", NotificationDeliveryPolicy.Default, Now);
        Assert.True(target.IsEnabled);
        Assert.Equal(0, target.ConsecutiveFailureCount);
        Assert.Null(target.DisabledReason);
        Assert.Null(target.AutoDisabledAt);
    }

    [Fact]
    public void DefaultSubscriptionsMatchUpstreamFailuresOnSuccessesOff()
    {
        var target = Target(NotificationChannel.Slack);

        Assert.Null(target.SubscribedEvents);
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.DeploymentFailed));
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.BackupFailed));
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.ServerUnreachable));
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.ServerDiskUsageHigh));
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.ApplicationRestartLimitReached));
        Assert.False(target.IsSubscribedTo(NotificationEventCatalog.DeploymentSucceeded));
        Assert.False(target.IsSubscribedTo(NotificationEventCatalog.BackupSucceeded));
        Assert.False(target.IsSubscribedTo(NotificationEventCatalog.ServerReachable));
        Assert.False(target.IsSubscribedTo("custom.unknown_event"));
    }

    [Fact]
    public void ExplicitSubscriptionsReplaceDefaultsButNeverAlwaysDeliveredEvents()
    {
        var target = Target(NotificationChannel.Telegram);

        target.SetSubscriptions([NotificationEventCatalog.DeploymentSucceeded, NotificationEventCatalog.ApiTokenFailing],
            Now);

        Assert.Equal([NotificationEventCatalog.DeploymentSucceeded], target.SubscribedEvents!);
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.DeploymentSucceeded));
        Assert.False(target.IsSubscribedTo(NotificationEventCatalog.DeploymentFailed));
        // Always-send events (upstream alwaysSendEvents) ignore subscriptions.
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.CertificateRenewalFailed));
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.ApiTokenFailing));
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.ChannelDisabled));

        target.SetSubscriptions(null, Now);
        Assert.True(target.IsSubscribedTo(NotificationEventCatalog.DeploymentFailed));
    }

    [Fact]
    public void UnknownSubscriptionsAndRoutesAreRejected()
    {
        var target = Target(NotificationChannel.Telegram);

        Assert.Throws<DomainException>(() => target.SetSubscriptions(["deployment.exploded"], Now));
        Assert.Throws<DomainException>(() => target.SetEventRoutes(
            new Dictionary<string, string> { ["nope"] = "1" }, Now));
        Assert.Throws<DomainException>(() => target.SetEventRoutes(
            new Dictionary<string, string> { [NotificationEventCatalog.DeploymentFailed] = "1\n2" }, Now));
    }

    [Fact]
    public void EventRoutesAreStoredPerEvent()
    {
        var target = Target(NotificationChannel.Telegram);

        target.SetEventRoutes(new Dictionary<string, string>
        {
            [NotificationEventCatalog.DeploymentFailed] = "42",
            [NotificationEventCatalog.BackupFailed] = " "
        }, Now);

        Assert.Equal("42", target.RouteFor(NotificationEventCatalog.DeploymentFailed));
        Assert.Null(target.RouteFor(NotificationEventCatalog.BackupFailed));
    }

    [Fact]
    public void ChannelDisabledNoticeIsNeverSentByEmail()
    {
        Assert.False(Target(NotificationChannel.Email).IsSubscribedTo(NotificationEventCatalog.ChannelDisabled));
        Assert.True(Target(NotificationChannel.Slack).IsSubscribedTo(NotificationEventCatalog.ChannelDisabled));
    }

    [Fact]
    public void CatalogKeysAreUniqueAndThrottlesAreWellFormed()
    {
        Assert.Equal(NotificationEventCatalog.All.Count,
            NotificationEventCatalog.All.Select(definition => definition.Key).Distinct().Count());
        foreach (var definition in NotificationEventCatalog.All)
        {
            if (definition.Throttle.Mode == NotificationThrottleMode.Interval)
                Assert.True(definition.Throttle.Interval > TimeSpan.Zero);
            if (definition.Releases is not null)
                Assert.True(NotificationEventCatalog.IsKnown(definition.Releases));
        }

        Assert.Equal(TimeSpan.FromHours(1),
            NotificationEventCatalog.Find(NotificationEventCatalog.ApplicationRestartLimitReached)!.Throttle.Interval);
        Assert.Equal(NotificationThrottleMode.OncePerIncident,
            NotificationEventCatalog.Find(NotificationEventCatalog.ServerUnreachable)!.Throttle.Mode);
    }

    [Fact]
    public void ThrottleSubjectKeyNeedsATargetResource()
    {
        Assert.Null(NotificationThrottle.SubjectKeyFor(NotificationTargetType.Server, null));
        Assert.Equal("Server:abc", NotificationThrottle.SubjectKeyFor(NotificationTargetType.Server, " abc "));
    }

    [Fact]
    public void InstanceEmailSettingsValidateProvidersAndServers()
    {
        var settings = InstanceEmailSettings.Create(Now);
        Assert.False(settings.IsEnabled);

        Assert.Throws<DomainException>(() => settings.Update(EmailProvider.Smtp, null, [], null, null, null, null,
            null, Now));
        Assert.Throws<DomainException>(() => settings.Update(EmailProvider.Resend, null, [], null,
            "noreply@example.com", null, null, null, Now));
        Assert.Throws<DomainException>(() => settings.Update(EmailProvider.Resend, null, [], "protected", null, null,
            null, null, Now));
        Assert.Throws<DomainException>(() => settings.Update(EmailProvider.Smtp, null, [Server("A"), Server("a")],
            null, null, null, null, null, Now));
        Assert.Throws<DomainException>(() => settings.Update(EmailProvider.Smtp, "Missing", [Server("A")], null, null,
            null, null, null, Now));

        settings.Update(EmailProvider.Smtp, null, [Server("Primary"), Server("Backup")], null, null, null,
            "alerts@example.com", "Alerts", Now);
        Assert.True(settings.IsEnabled);
        Assert.Equal("Primary", settings.ActiveSmtpServer!.Name);
        Assert.Equal("alerts@example.com", settings.NotificationFromAddress);

        settings.Update(EmailProvider.Resend, "Backup", [Server("Primary"), Server("Backup")], "protected-key",
            "Mail@Example.com", "Vessel", null, null, Now);
        Assert.True(settings.IsEnabled);
        Assert.Equal("mail@example.com", settings.ResendFromAddress);
    }

    [Theory]
    [InlineData("smtp.example.com", true)]
    [InlineData("SMTP.Example.COM.", true)]
    [InlineData("mail_server", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("[2001:db8::1]", true)]
    [InlineData("smtp.example.com:25", false)]
    [InlineData("smtp example.com", false)]
    [InlineData("-bad.example.com", false)]
    [InlineData("", false)]
    public void SmtpHostValidationFollowsUpstreamSafeSmtpHost(string host, bool valid)
    {
        Assert.Equal(valid, SmtpServerSettings.IsValidHost(SmtpServerSettings.NormalizeHost(host)));
    }

    [Fact]
    public void SmtpServerRejectsBadPortsTimeoutsAndHeaderInjectionInSenderNames()
    {
        Assert.Throws<DomainException>(() => SmtpServerSettings.Create("A", "smtp.example.com", 0,
            SmtpEncryption.StartTls, null, null, "a@example.com", null, null, null));
        Assert.Throws<DomainException>(() => SmtpServerSettings.Create("A", "smtp.example.com", 587,
            SmtpEncryption.StartTls, null, null, "a@example.com", null, null, 0));
        Assert.Throws<DomainException>(() => SmtpServerSettings.Create("A", "smtp.example.com", 587,
            SmtpEncryption.StartTls, null, null, "a@example.com", "Evil\r\nBcc: x@example.com", null, null));
        Assert.Throws<DomainException>(() => SmtpServerSettings.Create("A", "smtp.example.com", 587,
            SmtpEncryption.StartTls, null, null, "a@example.com", null, "bad ehlo", null));
    }

    private static SmtpServerSettings Server(string name)
    {
        return SmtpServerSettings.Create(name, "smtp.example.com", 587, SmtpEncryption.StartTls, "user", "protected",
            "noreply@example.com", "Vessel", "vessel.example.com", null);
    }

    private static NotificationTarget Target(NotificationChannel channel)
    {
        return NotificationTarget.Create(TeamId.New(), new ResourceName("Ops"), channel, SecretReferenceId.New(), Now);
    }
}
