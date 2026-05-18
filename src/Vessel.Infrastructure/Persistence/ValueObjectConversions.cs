using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Databases;
using Vessel.Domain.Notifications;
using Vessel.Domain.Secrets;
using Vessel.Domain.ValueObjects;

namespace Vessel.Infrastructure.Persistence;

internal static class ValueObjectConversions
{
    public static readonly ValueConverter<DisplayName, string> DisplayName = new(value => value.Value,
        value => new DisplayName(value));

    public static readonly ValueConverter<Description, string?> Description = new(value => value.Value,
        value => new Description(value));

    public static readonly ValueConverter<Description?, string?> NullableDescription = new(
        value => value.HasValue ? value.Value.Value : null,
        value => ParseNullableDescription(value));

    public static readonly ValueConverter<EmailAddress, string> EmailAddress = new(value => value.Value,
        value => new EmailAddress(value));

    public static readonly ValueConverter<ResourceName, string> ResourceName = new(value => value.Value,
        value => new ResourceName(value));

    public static readonly ValueConverter<Slug, string> Slug = new(value => value.Value, value => new Slug(value));

    public static readonly ValueConverter<DomainName, string> DomainName = new(value => value.Value,
        value => new DomainName(value));

    public static readonly ValueConverter<PortNumber, int> PortNumber = new(value => value.Value,
        value => new PortNumber(value));

    public static readonly ValueConverter<RepositoryUrl, string> RepositoryUrl = new(value => value.Value,
        value => new RepositoryUrl(value));

    public static readonly ValueConverter<ImageTag, string> ImageTag = new(value => value.Value,
        value => new ImageTag(value));

    public static readonly ValueConverter<VersionLabel, string> VersionLabel = new(value => value.Value,
        value => new VersionLabel(value));

    public static readonly ValueConverter<ServerAddress, string> ServerAddress = new(
        value => FormatServerAddress(value),
        value => ParseServerAddress(value));

    public static readonly ValueConverter<ResourceLimits, string> ResourceLimits = new(
        value => FormatResourceLimits(value),
        value => ParseResourceLimits(value));

    public static readonly ValueConverter<GitSource, string> GitSource = new(
        value => FormatGitSource(value),
        value => ParseGitSource(value));

    public static readonly ValueConverter<BuildConfiguration, string> BuildConfiguration = new(
        value => FormatBuildConfiguration(value),
        value => ParseBuildConfiguration(value));

    public static readonly ValueConverter<RuntimeConfiguration, string> RuntimeConfiguration = new(
        value => FormatRuntimeConfiguration(value),
        value => ParseRuntimeConfiguration(value));

    public static readonly ValueConverter<DeploymentSettings, string> DeploymentSettings = new(
        value => FormatDeploymentSettings(value),
        value => ParseDeploymentSettings(value));

    public static readonly ValueConverter<StorageConfiguration, string> StorageConfiguration = new(
        value => FormatStorageConfiguration(value),
        value => ParseStorageConfiguration(value));

    public static readonly ValueConverter<SecretPolicy, string> SecretPolicy = new(
        value => FormatSecretPolicy(value),
        value => ParseSecretPolicy(value));

    public static readonly ValueConverter<NotificationDeliveryPolicy, string> NotificationDeliveryPolicy = new(
        value => FormatNotificationDeliveryPolicy(value),
        value => ParseNotificationDeliveryPolicy(value));

    public static readonly ValueConverter<AuditTarget, string> AuditTarget = new(
        value => FormatAuditTarget(value),
        value => ParseAuditTarget(value));

    private static string FormatServerAddress(ServerAddress value)
    {
        return string.Join('|', value.User ?? string.Empty, value.Host,
            value.Port.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static Description? ParseNullableDescription(string? value)
    {
        return value == null ? null : new Description(value);
    }

    private static string FormatResourceLimits(ResourceLimits value)
    {
        return string.Join('|', value.Cpus?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            value.MemoryBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string FormatGitSource(GitSource value)
    {
        return string.Join('|', Escape(value.RepositoryUrl.Value), Escape(value.Branch),
            Escape(value.CommitSha ?? string.Empty));
    }

    private static string FormatBuildConfiguration(BuildConfiguration value)
    {
        return string.Join('|', ((int)value.BuildPack).ToString(CultureInfo.InvariantCulture),
            Escape(value.BaseDirectory), Escape(value.DockerfilePath ?? string.Empty),
            Escape(value.InstallCommand ?? string.Empty), Escape(value.BuildCommand ?? string.Empty),
            Escape(value.StartCommand ?? string.Empty), Escape(value.ImageTag?.Value ?? string.Empty));
    }

    private static string FormatRuntimeConfiguration(RuntimeConfiguration value)
    {
        return string.Join('|', value.ExposedPort?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            value.Limits.Cpus?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            value.Limits.MemoryBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            value.HealthCheckEnabled.ToString(), Escape(value.HealthCheckPath));
    }

    private static string FormatDeploymentSettings(DeploymentSettings value)
    {
        return string.Join('|', value.AutoDeployEnabled.ToString(), value.PreviewDeploymentsEnabled.ToString(),
            value.ForceRebuild.ToString(), Escape(value.WatchPaths ?? string.Empty));
    }

    private static string FormatStorageConfiguration(StorageConfiguration value)
    {
        return string.Join('|', Escape(value.VolumeName), Escape(value.MountPath),
            value.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string FormatSecretPolicy(SecretPolicy value)
    {
        return string.Join('|', value.ShowOnce.ToString(), value.AvailableAtBuild.ToString(),
            value.AvailableAtRuntime.ToString());
    }

    private static string FormatNotificationDeliveryPolicy(NotificationDeliveryPolicy value)
    {
        return string.Join('|', ((int)value.MinimumSeverity).ToString(CultureInfo.InvariantCulture),
            value.DeploymentEventsEnabled.ToString(), value.ServerEventsEnabled.ToString(),
            value.SecurityEventsEnabled.ToString());
    }

    private static string FormatAuditTarget(AuditTarget value)
    {
        return string.Join('|', Escape(value.Type), Escape(value.Id));
    }

    private static ServerAddress ParseServerAddress(string value)
    {
        var parts = value.Split('|');
        return new ServerAddress(parts[1], new PortNumber(int.Parse(parts[2], CultureInfo.InvariantCulture)),
            string.IsNullOrEmpty(parts[0]) ? null : parts[0]);
    }

    private static ResourceLimits ParseResourceLimits(string value)
    {
        var parts = value.Split('|');
        decimal? cpus = string.IsNullOrEmpty(parts[0]) ? null : decimal.Parse(parts[0], CultureInfo.InvariantCulture);
        long? memory = string.IsNullOrEmpty(parts[1]) ? null : long.Parse(parts[1], CultureInfo.InvariantCulture);

        return new ResourceLimits(cpus, memory);
    }

    private static GitSource ParseGitSource(string value)
    {
        var parts = SplitEscaped(value);
        return new GitSource(new RepositoryUrl(parts[0]), parts[1], string.IsNullOrEmpty(parts[2]) ? null : parts[2]);
    }

    private static BuildConfiguration ParseBuildConfiguration(string value)
    {
        var parts = SplitEscaped(value);
        return new BuildConfiguration(
            (ApplicationBuildPack)int.Parse(parts[0], CultureInfo.InvariantCulture),
            parts[1],
            EmptyToNull(parts[2]),
            EmptyToNull(parts[3]),
            EmptyToNull(parts[4]),
            EmptyToNull(parts[5]),
            string.IsNullOrEmpty(parts[6]) ? null : new ImageTag(parts[6]));
    }

    private static RuntimeConfiguration ParseRuntimeConfiguration(string value)
    {
        var parts = SplitEscaped(value);
        PortNumber? port = string.IsNullOrEmpty(parts[0])
            ? null
            : new PortNumber(int.Parse(parts[0], CultureInfo.InvariantCulture));
        decimal? cpus = string.IsNullOrEmpty(parts[1]) ? null : decimal.Parse(parts[1], CultureInfo.InvariantCulture);
        long? memory = string.IsNullOrEmpty(parts[2]) ? null : long.Parse(parts[2], CultureInfo.InvariantCulture);

        return new RuntimeConfiguration(port, new ResourceLimits(cpus, memory), bool.Parse(parts[3]), parts[4]);
    }

    private static DeploymentSettings ParseDeploymentSettings(string value)
    {
        var parts = SplitEscaped(value);
        return new DeploymentSettings(bool.Parse(parts[0]), bool.Parse(parts[1]), bool.Parse(parts[2]),
            EmptyToNull(parts[3]));
    }

    private static StorageConfiguration ParseStorageConfiguration(string value)
    {
        var parts = SplitEscaped(value);
        long? size = string.IsNullOrEmpty(parts[2]) ? null : long.Parse(parts[2], CultureInfo.InvariantCulture);

        return new StorageConfiguration(parts[0], parts[1], size);
    }

    private static SecretPolicy ParseSecretPolicy(string value)
    {
        var parts = value.Split('|');
        return new SecretPolicy(bool.Parse(parts[0]), bool.Parse(parts[1]), bool.Parse(parts[2]));
    }

    private static NotificationDeliveryPolicy ParseNotificationDeliveryPolicy(string value)
    {
        var parts = value.Split('|');
        return new NotificationDeliveryPolicy((NotificationSeverity)int.Parse(parts[0], CultureInfo.InvariantCulture),
            bool.Parse(parts[1]), bool.Parse(parts[2]), bool.Parse(parts[3]));
    }

    private static AuditTarget ParseAuditTarget(string value)
    {
        var parts = SplitEscaped(value);
        return new AuditTarget(parts[0], parts[1]);
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private static string[] SplitEscaped(string value)
    {
        return value.Split('|').Select(Uri.UnescapeDataString).ToArray();
    }
}
