using Vessel.Domain;
using Vessel.Domain.Access;

namespace Vessel.Application.Authorization;

/// <summary>
///     Where a team-owned target sits: team → project → environment → resource. Grants on a parent apply to every
///     child, so a check walks the location from the team down.
/// </summary>
public readonly record struct ResourceLocation(
    TeamId TeamId,
    ProjectId? ProjectId = null,
    EnvironmentId? EnvironmentId = null,
    Guid? ResourceId = null);

/// <summary>
///     The grants of one actor (a member, or the token of the current request) in one team, with targets resolved
///     inside that team. Grants that point outside the team are dropped. Levels are already expanded.
/// </summary>
public sealed class AccessLayer
{
    private readonly IReadOnlyDictionary<EnvironmentId, IReadOnlySet<AccessLevel>> _environmentLevels;
    private readonly IReadOnlyDictionary<ProjectId, IReadOnlySet<AccessLevel>> _projectLevels;
    private readonly IReadOnlyDictionary<Guid, IReadOnlySet<AccessLevel>> _resourceLevels;
    private readonly IReadOnlySet<AccessLevel> _teamLevels;

    public AccessLayer(
        IReadOnlySet<AccessLevel> teamLevels,
        IReadOnlyDictionary<ProjectId, IReadOnlySet<AccessLevel>> projectLevels,
        IReadOnlyDictionary<EnvironmentId, IReadOnlySet<AccessLevel>> environmentLevels,
        IReadOnlyDictionary<Guid, IReadOnlySet<AccessLevel>> resourceLevels,
        IReadOnlySet<ProjectId> visibleProjects,
        IReadOnlySet<EnvironmentId> visibleEnvironments)
    {
        _teamLevels = teamLevels;
        _projectLevels = projectLevels;
        _environmentLevels = environmentLevels;
        _resourceLevels = resourceLevels;
        VisibleProjects = visibleProjects;
        VisibleEnvironments = visibleEnvironments;
    }

    /// <summary>Projects the actor can open: granted projects and parents of granted environments and resources.</summary>
    public IReadOnlySet<ProjectId> VisibleProjects { get; }

    /// <summary>Environments the actor can open: granted ones, those in granted projects, parents of granted resources.</summary>
    public IReadOnlySet<EnvironmentId> VisibleEnvironments { get; }

    /// <summary>True when a team-wide grant exists, so every project in the team is visible.</summary>
    public bool CoversWholeTeam => _teamLevels.Count > 0;

    public bool AllowsTeamWide(AccessLevel level)
    {
        return _teamLevels.Contains(level);
    }

    public bool Allows(AccessLevel level, ResourceLocation location)
    {
        if (_teamLevels.Contains(level)) return true;
        if (location.ProjectId is { } project && Has(_projectLevels, project, level)) return true;
        if (location.EnvironmentId is { } environment && Has(_environmentLevels, environment, level)) return true;
        return location.ResourceId is { } resource && Has(_resourceLevels, resource, level);
    }

    /// <summary>True when the level is granted anywhere (team, any project, environment or resource).</summary>
    public bool AllowsSomewhere(AccessLevel level)
    {
        return _teamLevels.Contains(level)
               || _projectLevels.Values.Any(levels => levels.Contains(level))
               || _environmentLevels.Values.Any(levels => levels.Contains(level))
               || _resourceLevels.Values.Any(levels => levels.Contains(level));
    }

    public bool CanOpenProject(ProjectId projectId)
    {
        return CoversWholeTeam || VisibleProjects.Contains(projectId);
    }

    public bool CanOpenEnvironment(EnvironmentId environmentId)
    {
        return CoversWholeTeam || VisibleEnvironments.Contains(environmentId);
    }

    private static bool Has<TKey>(IReadOnlyDictionary<TKey, IReadOnlySet<AccessLevel>> levels, TKey key,
        AccessLevel level)
        where TKey : notnull
    {
        return levels.TryGetValue(key, out IReadOnlySet<AccessLevel>? granted) && granted.Contains(level);
    }
}
