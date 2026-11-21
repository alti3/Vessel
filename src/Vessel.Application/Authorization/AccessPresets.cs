using Vessel.Domain;
using Vessel.Domain.Access;

namespace Vessel.Application.Authorization;

/// <summary>
///     Named grant presets for members, invitations and tokens (Coolify <c>Team/Member/Access::presetGrants</c>
///     parity): <c>viewer</c> (team-wide read), <c>developer</c> (write + deploy on one project), <c>operator</c>
///     (team-wide write, deploy and delete) and <c>full</c> (team-wide root).
/// </summary>
public static class AccessPresets
{
    public const string Viewer = "viewer";
    public const string Developer = "developer";
    public const string Operator = "operator";
    public const string Full = "full";

    public static IReadOnlyList<string> Names { get; } = [Viewer, Developer, Operator, Full];

    /// <summary>The grants of a preset; throws for unknown presets or a developer preset without a project.</summary>
    public static IReadOnlyList<AccessGrant> Grants(string preset, ProjectId? projectId = null)
    {
        return preset switch
        {
            Viewer => [AccessGrant.Team(AccessLevel.Read)],
            Developer when projectId.HasValue =>
                [AccessGrant.Project(projectId.Value, AccessLevel.Write, AccessLevel.Deploy)],
            Developer => throw new InvalidOperationException("The developer preset needs a project."),
            Operator => [AccessGrant.Team(AccessLevel.Write, AccessLevel.Deploy, AccessLevel.Delete)],
            Full => [AccessGrant.Team(AccessLevel.Root)],
            _ => throw new InvalidOperationException($"Unknown access preset '{preset}'.")
        };
    }
}
