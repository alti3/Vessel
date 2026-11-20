using Vessel.Domain;
using Vessel.Domain.Access;

namespace Vessel.Application.Authorization;

/// <summary>
///     What an actor can see in one team, for filtering list queries (Coolify <c>VisibleThroughAccessGrants</c>
///     parity). Scoped actors see only the projects, environments and resources their grants reach (and the parents
///     of granted children), the deployments of visible applications, and the servers that run visible resources.
/// </summary>
public sealed class AccessVisibility
{
    private readonly IReadOnlyDictionary<EnvironmentId, ProjectId> _environmentProjects;
    private readonly IReadOnlyList<AccessLayer> _layers;
    private readonly IReadOnlySet<Guid> _servers;
    private readonly bool _unrestricted;

    public AccessVisibility(
        TeamId teamId,
        IReadOnlyList<AccessLayer> layers,
        IReadOnlyDictionary<EnvironmentId, ProjectId> environmentProjects,
        IReadOnlySet<Guid> servers)
        : this(teamId, layers, environmentProjects, servers, false, false)
    {
    }

    private AccessVisibility(
        TeamId teamId,
        IReadOnlyList<AccessLayer> layers,
        IReadOnlyDictionary<EnvironmentId, ProjectId> environmentProjects,
        IReadOnlySet<Guid> servers,
        bool unrestricted,
        bool none)
    {
        TeamId = teamId;
        _layers = layers;
        _environmentProjects = environmentProjects;
        _servers = servers;
        _unrestricted = unrestricted;
        IsEmpty = none;
    }

    public TeamId TeamId { get; }

    /// <summary>True when every layer covers the whole team, so no filtering applies.</summary>
    public bool IsUnrestricted => _unrestricted;

    /// <summary>True when the actor has no access to the team at all.</summary>
    public bool IsEmpty { get; }

    public static AccessVisibility Unrestricted(TeamId teamId)
    {
        return new AccessVisibility(teamId, [], new Dictionary<EnvironmentId, ProjectId>(), new HashSet<Guid>(),
            true, false);
    }

    public static AccessVisibility None(TeamId teamId)
    {
        return new AccessVisibility(teamId, [], new Dictionary<EnvironmentId, ProjectId>(), new HashSet<Guid>(),
            false, true);
    }

    public bool CanSeeProject(Guid projectId)
    {
        if (_unrestricted) return true;
        return !IsEmpty && _layers.All(layer => layer.CanOpenProject(new ProjectId(projectId)));
    }

    public bool CanSeeEnvironment(Guid environmentId)
    {
        if (_unrestricted) return true;
        return !IsEmpty && _layers.All(layer => layer.CanOpenEnvironment(new EnvironmentId(environmentId)));
    }

    /// <summary>A resource (application, database or service) is visible when every layer allows Read on it.</summary>
    public bool CanSeeResource(Guid environmentId, Guid resourceId)
    {
        if (_unrestricted) return true;
        if (IsEmpty || !_environmentProjects.TryGetValue(new EnvironmentId(environmentId), out ProjectId projectId))
            return false;

        var location = new ResourceLocation(TeamId, projectId, new EnvironmentId(environmentId), resourceId);
        return _layers.All(layer => layer.Allows(AccessLevel.Read, location));
    }

    public bool CanSeeServer(Guid serverId)
    {
        return _unrestricted || (!IsEmpty && _servers.Contains(serverId));
    }
}
