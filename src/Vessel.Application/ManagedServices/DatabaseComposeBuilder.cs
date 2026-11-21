using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vessel.Domain.Databases;

namespace Vessel.Application.ManagedServices;

/// <summary>
///     Builds the Compose project for a managed database: pinned image, password passed through the <c>.env</c> file
///     (see <see cref="ComposeEnvironmentFile" />), named data volume, Vessel labels and the engine health check.
/// </summary>
public static class DatabaseComposeBuilder
{
    private static readonly JsonSerializerOptions YamlScalarOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ProjectName(DatabaseResource database)
    {
        return $"vessel-db-{database.Id.Value:N}"[..39];
    }

    public static string ContainerName(DatabaseResource database)
    {
        // "vessel-db-" + 32 hex digits = 42 characters (the previous [..50] slice threw).
        return $"vessel-db-{database.Id.Value:N}";
    }

    public static DatabaseProvisioningPlan CreatePlan(DatabaseResource database, string credentials)
    {
        DatabaseEngineProfile profile = DatabaseEngineProfiles.For(database.Engine);
        var service = Slug(database.Name.Value);
        var containerName = ContainerName(database);
        var image = DatabaseEngineProfiles.EffectiveImage(database);
        var builder = new StringBuilder();
        builder.AppendLine("services:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  {service}:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    image: {Scalar(image)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    container_name: {containerName}");
        builder.AppendLine("    environment:");
        foreach (var (key, value) in profile.FixedEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.AppendLine(CultureInfo.InvariantCulture, $"      {key}: {Scalar(value)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      {profile.PasswordVariable}: \"${{{profile.PasswordVariable}}}\"");
        if (profile.Command is { } command)
            builder.AppendLine(CultureInfo.InvariantCulture, $"    command: [{string.Join(", ", command.Select(Scalar))}]");
        builder.AppendLine("    volumes:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      - {database.Storage.VolumeName}:{database.Storage.MountPath}");
        builder.AppendLine("    labels:");
        builder.AppendLine("      vessel.managed: \"true\"");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      vessel.database: \"{database.Id.Value:D}\"");
        DatabaseHealthCheckPolicy health = database.HealthCheck;
        if (health.Enabled)
        {
            builder.AppendLine("    healthcheck:");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      test: [\"CMD-SHELL\", {Scalar(profile.HealthProbe)}]");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      interval: {health.IntervalSeconds}s");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      timeout: {health.TimeoutSeconds}s");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      retries: {health.Retries}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      start_period: {health.StartPeriodSeconds}s");
        }
        else
        {
            builder.AppendLine("    healthcheck:");
            builder.AppendLine("      disable: true");
        }

        builder.AppendLine("    restart: unless-stopped");
        builder.AppendLine("volumes:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  {database.Storage.VolumeName}:");
        return new DatabaseProvisioningPlan(ProjectName(database), service, containerName,
            database.Storage.VolumeName, builder.ToString(),
            new Dictionary<string, string> { [profile.PasswordVariable] = credentials });
    }

    /// <summary>A JSON string literal, which is also a valid YAML double-quoted scalar.</summary>
    private static string Scalar(string value)
    {
        return JsonSerializer.Serialize(value, YamlScalarOptions);
    }

    private static string Slug(string value)
    {
        string normalized = new(value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        normalized = normalized.Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "database" : normalized[..Math.Min(40, normalized.Length)];
    }
}
