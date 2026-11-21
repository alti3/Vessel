using Vessel.Application.Docker;
using Vessel.Domain.Monitoring;

namespace Vessel.Application.Resources.Status;

/// <summary>A Vessel resource a container belongs to.</summary>
public readonly record struct RuntimeResourceKey(ResourceRuntimeKind Kind, Guid Id);

/// <summary>
///     Maps containers to Vessel resources by the labels Vessel writes: <c>vessel.application</c> (application
///     containers, every compose service included; preview containers also carry <c>vessel.preview</c>),
///     <c>vessel.database</c> (managed databases) and the compose project <c>vessel-svc-…</c> (service templates).
/// </summary>
public static class RuntimeResourceLabels
{
    public const string Managed = "vessel.managed";
    public const string Application = "vessel.application";
    public const string Preview = "vessel.preview";
    public const string Database = "vessel.database";
    public const string Proxy = "vessel.proxy";
    public const string ComposeProject = "com.docker.compose.project";

    /// <summary>The compose project of a service template deployment (see <c>ServiceTemplateCatalog</c>).</summary>
    public static string ServiceProjectName(Guid serviceId)
    {
        return $"vessel-svc-{serviceId:N}"[..39];
    }

    public static bool IsPreview(IReadOnlyDictionary<string, string> labels)
    {
        return labels.ContainsKey(Preview);
    }

    /// <summary>
    ///     Resolves the production resource of a container, or null. Service projects are matched against
    ///     <paramref name="serviceProjects" /> (project name to service id), because the project name truncates the id.
    /// </summary>
    public static RuntimeResourceKey? Resolve(IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, Guid> serviceProjects)
    {
        if (labels.TryGetValue(Application, out var application) && Guid.TryParse(application, out var applicationId))
            return IsPreview(labels) ? null : new RuntimeResourceKey(ResourceRuntimeKind.Application, applicationId);
        if (labels.TryGetValue(Database, out var database) && Guid.TryParse(database, out var databaseId))
            return new RuntimeResourceKey(ResourceRuntimeKind.Database, databaseId);
        if (labels.TryGetValue(ComposeProject, out var project) && serviceProjects.TryGetValue(project, out var serviceId))
            return new RuntimeResourceKey(ResourceRuntimeKind.Service, serviceId);
        return null;
    }

    /// <summary>True for any container Vessel manages or that belongs to a Vessel resource or the proxy.</summary>
    public static bool IsVesselContainer(ContainerStateReading container)
    {
        IReadOnlyDictionary<string, string> labels = container.Labels;
        return labels.ContainsKey(Managed) || labels.ContainsKey(Application) || labels.ContainsKey(Database) ||
               labels.ContainsKey(Proxy) ||
               (labels.TryGetValue(ComposeProject, out var project) &&
                project.StartsWith("vessel-", StringComparison.Ordinal));
    }
}
