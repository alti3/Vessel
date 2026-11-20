using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vessel.Domain;
using Vessel.Domain.Notifications;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Model configuration for NTF-03/NTF-04/NTF-05 (target failure tracking, subscriptions, throttles) and NTF-06
///     (instance email settings). Kept separate from <see cref="VesselDbContext" /> so the additions stay reviewable.
/// </summary>
internal static class NotificationEmailModelConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ValueConverter<IReadOnlyList<SmtpServerSettings>, string> SmtpServersConverter = new(
        servers => JsonSerializer.Serialize(servers, JsonOptions),
        json => JsonSerializer.Deserialize<SmtpServerSettings[]>(json, JsonOptions) ?? Array.Empty<SmtpServerSettings>());

    private static readonly ValueComparer<IReadOnlyList<SmtpServerSettings>> SmtpServersComparer = new(
        (left, right) => left != null && right != null && left.SequenceEqual(right),
        servers => servers.Aggregate(0, (hash, server) => HashCode.Combine(hash, server.GetHashCode())),
        servers => servers.ToArray());

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationTarget>(builder =>
        {
            builder.Property(target => target.SubscribedEvents).HasColumnType("text[]");
            builder.Property(target => target.EventRoutesJson).HasColumnType("jsonb").HasDefaultValue("{}")
                .IsRequired();
            builder.Property(target => target.ConsecutiveFailureCount).HasDefaultValue(0).IsRequired();
            builder.Property(target => target.DisabledReason).HasMaxLength(512);
            builder.Property(target => target.AutoDisabledAt);
            builder.Ignore(target => target.EffectiveSubscriptions);
        });

        modelBuilder.Entity<NotificationThrottle>(builder =>
        {
            builder.ToTable("notification_throttles");
            builder.HasKey(throttle => throttle.Id);
            builder.Property(throttle => throttle.Id)
                .HasConversion(id => id.Value, value => new NotificationThrottleId(value));
            builder.Property(throttle => throttle.TeamId)
                .HasConversion(id => id.Value, value => new TeamId(value));
            builder.Property(throttle => throttle.EventType).HasMaxLength(NotificationThrottle.MaxEventTypeLength)
                .IsRequired();
            builder.Property(throttle => throttle.SubjectKey).HasMaxLength(NotificationThrottle.MaxSubjectKeyLength)
                .IsRequired();
            builder.Property(throttle => throttle.SentAt).IsRequired();
            builder.Property(throttle => throttle.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(throttle => throttle.DomainEvents);
            builder.HasIndex(throttle => new { throttle.TeamId, throttle.EventType, throttle.SubjectKey }).IsUnique();
            builder.HasIndex(throttle => throttle.SentAt);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(throttle => throttle.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InstanceEmailSettings>(builder =>
        {
            builder.ToTable("instance_email_settings");
            builder.HasKey(settings => settings.Id);
            builder.Property(settings => settings.Id)
                .HasConversion(id => id.Value, value => new InstanceEmailSettingsId(value))
                .ValueGeneratedNever();
            builder.Property(settings => settings.Provider).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(settings => settings.ActiveSmtpServerName).HasMaxLength(SmtpServerSettings.MaxNameLength);
            // Server entries carry only an encrypted password envelope, never the plaintext.
            builder.Property(settings => settings.SmtpServers)
                .HasConversion(SmtpServersConverter, SmtpServersComparer)
                .HasColumnType("jsonb")
                .IsRequired();
            builder.Property(settings => settings.ProtectedResendApiKey).HasMaxLength(2048);
            builder.Property(settings => settings.ResendFromAddress).HasMaxLength(320);
            builder.Property(settings => settings.ResendFromName).HasMaxLength(255);
            builder.Property(settings => settings.NotificationFromAddress).HasMaxLength(320);
            builder.Property(settings => settings.NotificationFromName).HasMaxLength(255);
            builder.Property(settings => settings.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(settings => settings.ActiveSmtpServer);
            builder.Ignore(settings => settings.IsEnabled);
            builder.Ignore(settings => settings.DomainEvents);
        });
    }
}
