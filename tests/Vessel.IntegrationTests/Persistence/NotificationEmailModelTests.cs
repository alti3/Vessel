using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;
using Vessel.Infrastructure.Persistence;

namespace Vessel.IntegrationTests.Persistence;

/// <summary>Model shape for NTF-03/04/05 and NTF-06 (the consolidated migration is generated separately).</summary>
public sealed class NotificationEmailModelTests
{
    [Fact]
    public void ThrottlesHaveAUniqueTeamEventSubjectKey()
    {
        using VesselDbContext context = CreateContext();
        var throttle = context.Model.FindEntityType(typeof(NotificationThrottle));

        Assert.Equal("notification_throttles", throttle?.GetTableName());
        var unique = Assert.Single(throttle!.GetIndexes(), index => index.IsUnique);
        Assert.Equal([nameof(NotificationThrottle.TeamId), nameof(NotificationThrottle.EventType),
            nameof(NotificationThrottle.SubjectKey)], unique.Properties.Select(property => property.Name));
    }

    [Fact]
    public void TargetsTrackFailuresAndSubscriptions()
    {
        using VesselDbContext context = CreateContext();
        var target = context.Model.FindEntityType(typeof(NotificationTarget))!;

        Assert.Equal("text[]", target.FindProperty(nameof(NotificationTarget.SubscribedEvents))?.GetColumnType());
        Assert.True(target.FindProperty(nameof(NotificationTarget.SubscribedEvents))?.IsNullable);
        Assert.Equal("jsonb", target.FindProperty(nameof(NotificationTarget.EventRoutesJson))?.GetColumnType());
        Assert.NotNull(target.FindProperty(nameof(NotificationTarget.ConsecutiveFailureCount)));
        Assert.Equal(512, target.FindProperty(nameof(NotificationTarget.DisabledReason))?.GetMaxLength());
        Assert.NotNull(target.FindProperty(nameof(NotificationTarget.AutoDisabledAt)));
    }

    [Fact]
    public void InstanceEmailSettingsStoreServersAsJsonAndNoPlaintextSecretColumns()
    {
        using VesselDbContext context = CreateContext();
        var settings = context.Model.FindEntityType(typeof(InstanceEmailSettings))!;

        Assert.Equal("instance_email_settings", settings.GetTableName());
        Assert.Equal("jsonb", settings.FindProperty(nameof(InstanceEmailSettings.SmtpServers))?.GetColumnType());
        Assert.NotNull(settings.FindProperty(nameof(InstanceEmailSettings.ResendFromAddress)));
        Assert.NotNull(settings.FindProperty(nameof(InstanceEmailSettings.NotificationFromAddress)));
        Assert.DoesNotContain(settings.GetProperties(), property =>
            property.Name.Contains("Password", StringComparison.Ordinal) ||
            property.Name.Equals("ResendApiKey", StringComparison.Ordinal));
    }

    private static VesselDbContext CreateContext()
    {
        DbContextOptions<VesselDbContext> options = new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=vessel_tests;Username=vessel;Password=vessel")
            .Options;

        return new VesselDbContext(options);
    }
}
