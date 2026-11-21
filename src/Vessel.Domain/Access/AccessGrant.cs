using Vessel.Domain.Common;

namespace Vessel.Domain.Access;

/// <summary>
///     One access grant: a scope (the whole team, one project, one environment, or one resource such as an
///     application or database) and the levels it gives there. Grants are stored on team memberships, invitations and
///     API tokens; a grant on a parent applies to its children and the levels of all matching grants add up.
/// </summary>
public sealed record AccessGrant
{
    public const int MaxGrantsPerActor = 200;

    public AccessGrant(AccessScope scope, Guid? targetId, IEnumerable<AccessLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (!Enum.IsDefined(scope)) throw new DomainException("Access scope is not supported.");
        if (scope == AccessScope.Team && targetId is not null)
            throw new DomainException("A team-wide access grant cannot name a target.");
        if (scope != AccessScope.Team && (targetId is null || targetId == Guid.Empty))
            throw new DomainException($"A {scope.ToString().ToLowerInvariant()} access grant needs a target.");

        AccessLevel[] distinct = levels.Distinct().Order().ToArray();
        if (distinct.Any(level => !Enum.IsDefined(level))) throw new DomainException("Access level is not supported.");

        Scope = scope;
        TargetId = targetId;
        Levels = distinct;
    }

    public AccessScope Scope { get; }

    public Guid? TargetId { get; }

    public IReadOnlyList<AccessLevel> Levels { get; }

    public static AccessGrant Team(params AccessLevel[] levels)
    {
        return new AccessGrant(AccessScope.Team, null, levels);
    }

    public static AccessGrant Project(ProjectId projectId, params AccessLevel[] levels)
    {
        return new AccessGrant(AccessScope.Project, projectId.Value, levels);
    }

    public static AccessGrant Environment(EnvironmentId environmentId, params AccessLevel[] levels)
    {
        return new AccessGrant(AccessScope.Environment, environmentId.Value, levels);
    }

    public static AccessGrant Resource(Guid resourceId, params AccessLevel[] levels)
    {
        return new AccessGrant(AccessScope.Resource, resourceId, levels);
    }

    /// <summary>Validates and copies a grant list. <c>null</c> stays <c>null</c> and means "use the default".</summary>
    public static IReadOnlyList<AccessGrant>? Normalize(IEnumerable<AccessGrant>? grants)
    {
        if (grants is null) return null;
        AccessGrant[] list = grants.ToArray();
        if (list.Any(grant => grant is null)) throw new DomainException("Access grants cannot contain empty entries.");
        if (list.Length > MaxGrantsPerActor)
            throw new DomainException($"At most {MaxGrantsPerActor} access grants are allowed.");
        return list;
    }

    public bool Equals(AccessGrant? other)
    {
        return other is not null
               && Scope == other.Scope
               && TargetId == other.TargetId
               && Levels.SequenceEqual(other.Levels);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Scope);
        hash.Add(TargetId);
        foreach (AccessLevel level in Levels) hash.Add(level);
        return hash.ToHashCode();
    }
}
