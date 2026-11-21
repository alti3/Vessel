using Vessel.Application.Deployments;
using Vessel.Application.Docker;
using Vessel.Application.Resources.Status;

namespace Vessel.Application.Servers.DockerCleanup;

/// <summary>What Vessel knows about the resources whose containers and images a cleanup must respect.</summary>
/// <param name="ApplicationIds">Every existing application (any server).</param>
/// <param name="DatabaseIds">Every existing (not deleted) managed database.</param>
/// <param name="ServiceProjects">Compose project name to service id of every existing service.</param>
/// <param name="RetainedImages">
///     Image references that must stay: the current and rollback images of every application
///     (<c>ImagesToKeep</c> most recent successful production deployments).
/// </param>
public sealed record DockerCleanupInventory(
    IReadOnlySet<Guid> ApplicationIds,
    IReadOnlySet<Guid> DatabaseIds,
    IReadOnlyDictionary<string, Guid> ServiceProjects,
    IReadOnlySet<string> RetainedImages);

public sealed record PlannedImageRemoval(string Reference, long ReclaimableBytes, string Reason);

public sealed record DockerCleanupPlan(
    IReadOnlyList<ContainerStateReading> ContainersToRemove,
    IReadOnlyList<PlannedImageRemoval> ImagesToRemove);

/// <summary>
///     Pure cleanup planning (SRV-07, upstream <c>CleanupDocker</c> semantics adapted to Vessel labels):
///     <list type="bullet">
///         <item>Stopped Vessel containers are removed only when they belong to no existing resource (orphans of deleted
///         resources, or managed helpers); the proxy and every container of an existing application, database or service
///         stay, so a stopped or restart-limited resource remains visible.</item>
///         <item>Application images (<c>vessel/&lt;app&gt;:&lt;tag&gt;</c>) beyond the retained rollback targets are
///         removed; images of deleted applications are removed; preview tags are left to the preview lifecycle.</item>
///         <item>Other images are removed only when no container (running or stopped) uses them, they are not labelled
///         <c>vessel.managed=true</c>, and they are older than <see cref="NewImageGrace" /> (a deployment may be about
///         to use them).</item>
///         <item>Volumes are never part of the plan.</item>
///     </list>
/// </summary>
public static class DockerCleanupPlanner
{
    public static readonly TimeSpan NewImageGrace = TimeSpan.FromHours(1);

    public static DockerCleanupPlan Plan(IReadOnlyList<ContainerStateReading> containers,
        IReadOnlyList<ImageDetails> images, DockerCleanupInventory inventory, DateTimeOffset now)
    {
        var removeContainers = containers.Where(container => IsRemovableContainer(container, inventory)).ToArray();
        var removedIds = removeContainers.Select(container => container.Id).ToHashSet(StringComparer.Ordinal);
        var imagesInUse = containers.Where(container => !removedIds.Contains(container.Id))
            .SelectMany(container => new[] { container.ImageId, container.Image })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        var removals = new List<PlannedImageRemoval>();
        foreach (ImageDetails image in images)
        {
            if (imagesInUse.Contains(image.Id) || image.RepoTags.Any(imagesInUse.Contains)) continue;
            if (image.RepoTags.Count == 0) continue; // Dangling images go through "image prune".

            var removableTags = new List<(string Tag, string Reason)>();
            foreach (var tag in image.RepoTags)
            {
                var reason = TagRemovalReason(tag, image, inventory, now);
                if (reason is not null) removableTags.Add((tag, reason));
            }

            // Space is only reclaimed when the last tag of the image goes.
            var allRemoved = removableTags.Count == image.RepoTags.Count;
            for (var index = 0; index < removableTags.Count; index++)
                removals.Add(new PlannedImageRemoval(removableTags[index].Tag,
                    allRemoved && index == removableTags.Count - 1 ? image.SizeBytes : 0,
                    removableTags[index].Reason));
        }

        return new DockerCleanupPlan(removeContainers, removals);
    }

    public static bool IsRemovableContainer(ContainerStateReading container, DockerCleanupInventory inventory)
    {
        if (container.State is not ("exited" or "created" or "dead")) return false;
        if (!RuntimeResourceLabels.IsVesselContainer(container)) return false;
        IReadOnlyDictionary<string, string> labels = container.Labels;
        if (labels.ContainsKey(RuntimeResourceLabels.Proxy)) return false;

        if (labels.TryGetValue(RuntimeResourceLabels.Application, out var application))
            return Guid.TryParse(application, out var applicationId) && !inventory.ApplicationIds.Contains(applicationId);
        if (labels.TryGetValue(RuntimeResourceLabels.Database, out var database))
            return Guid.TryParse(database, out var databaseId) && !inventory.DatabaseIds.Contains(databaseId);
        if (labels.TryGetValue(RuntimeResourceLabels.ComposeProject, out var project) &&
            project.StartsWith("vessel-svc-", StringComparison.Ordinal))
            return !inventory.ServiceProjects.ContainsKey(project);
        if (labels.TryGetValue(RuntimeResourceLabels.ComposeProject, out var other) &&
            other.StartsWith("vessel-", StringComparison.Ordinal))
            return false; // Application and proxy compose projects are owned by their lifecycles.

        // A managed container that belongs to no resource (a finished helper).
        return labels.TryGetValue(RuntimeResourceLabels.Managed, out var managed) && managed == "true";
    }

    private static string? TagRemovalReason(string tag, ImageDetails image, DockerCleanupInventory inventory,
        DateTimeOffset now)
    {
        if (inventory.RetainedImages.Contains(tag)) return null;
        if (TryApplicationRepository(tag, out var applicationId, out var imageTag))
        {
            if (!inventory.ApplicationIds.Contains(applicationId)) return "image of a deleted application";
            if (imageTag.StartsWith("pr-", StringComparison.Ordinal)) return null;
            return "application image beyond the retained rollback targets";
        }

        if (image.Labels.TryGetValue(RuntimeResourceLabels.Managed, out var managed) && managed == "true") return null;
        if (image.CreatedAt is { } created && now - created < NewImageGrace) return null;
        return "unused image";
    }

    /// <summary>Parses <c>vessel/{applicationId:N}:{tag}</c> (see <see cref="DeploymentPlanBuilder.ImageRepository" />).</summary>
    public static bool TryApplicationRepository(string reference, out Guid applicationId, out string tag)
    {
        applicationId = Guid.Empty;
        tag = string.Empty;
        if (!reference.StartsWith("vessel/", StringComparison.Ordinal)) return false;
        var colon = reference.LastIndexOf(':');
        if (colon < 0) return false;
        tag = reference[(colon + 1)..];
        return Guid.TryParseExact(reference["vessel/".Length..colon], "N", out applicationId);
    }
}
