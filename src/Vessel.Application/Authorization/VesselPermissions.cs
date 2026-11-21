using Vessel.Domain.Access;
using Vessel.Domain.Teams;

namespace Vessel.Application.Authorization;

/// <summary>Where a permission can be granted.</summary>
public enum PermissionReach
{
    /// <summary>Granted on a team, project, environment or resource; grants on parents apply to children.</summary>
    Located,

    /// <summary>Team-level operation: needs the level on the whole team (scoped grants never suffice).</summary>
    TeamWide,

    /// <summary>Team administration: needs the Owner or Admin role (grants never suffice) and the level team-wide.</summary>
    TeamAdmin,

    /// <summary>Instance administration: needs an instance administrator, independent of team roles.</summary>
    InstanceAdmin
}

/// <summary>The access level and reach a permission requires.</summary>
public sealed record PermissionRule(AccessLevel Level, PermissionReach Reach);

public static class VesselPermissions
{
    public const string ProjectsRead = "projects.read";
    public const string ProjectsWrite = "projects.write";
    public const string ProjectsDelete = "projects.delete";
    public const string ServersRead = "servers.read";
    public const string ServersWrite = "servers.write";
    public const string ApplicationsRead = "applications.read";
    public const string ApplicationsWrite = "applications.write";
    public const string ApplicationsDelete = "applications.delete";
    public const string DeploymentsStart = "deployments.start";
    public const string DeploymentsCancel = "deployments.cancel";
    public const string DeploymentsReadLogs = "deployments.readLogs";
    public const string ResourcesLifecycle = "resources.lifecycle";

    /// <summary>
    ///     Runtime (container) logs of applications, databases and services (SRV-05). Read-sensitive like upstream
    ///     (<c>read:sensitive</c>, "users who can update the resource"): read-only members do not hold it.
    /// </summary>
    public const string ResourcesReadLogs = "resources.readLogs";
    public const string TerminalsOpen = "terminals.open";
    public const string SecretsRead = "secrets.read";
    public const string SecretsWrite = "secrets.write";
    public const string SettingsManage = "settings.manage";
    public const string TeamsManage = "teams.manage";
    public const string NotificationsRead = "notifications.read";
    public const string NotificationsWrite = "notifications.write";
    public const string InstanceManage = "instance.manage";

    /// <summary>
    ///     The permission catalog. Destructive commands need <see cref="AccessLevel.Delete" />; deploy, cancel and
    ///     start/stop/restart need <see cref="AccessLevel.Deploy" />; secrets need
    ///     <see cref="AccessLevel.ReadSensitive" /> (Coolify v4.5/v4.6 token ability parity).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, PermissionRule> Rules =
        new Dictionary<string, PermissionRule>(StringComparer.Ordinal)
        {
            [ProjectsRead] = new(AccessLevel.Read, PermissionReach.Located),
            [ProjectsWrite] = new(AccessLevel.Write, PermissionReach.Located),
            [ProjectsDelete] = new(AccessLevel.Delete, PermissionReach.Located),
            [ServersRead] = new(AccessLevel.Read, PermissionReach.Located),
            [ServersWrite] = new(AccessLevel.Write, PermissionReach.TeamWide),
            [ApplicationsRead] = new(AccessLevel.Read, PermissionReach.Located),
            [ApplicationsWrite] = new(AccessLevel.Write, PermissionReach.Located),
            [ApplicationsDelete] = new(AccessLevel.Delete, PermissionReach.Located),
            [DeploymentsStart] = new(AccessLevel.Deploy, PermissionReach.Located),
            [DeploymentsCancel] = new(AccessLevel.Deploy, PermissionReach.Located),
            [DeploymentsReadLogs] = new(AccessLevel.Read, PermissionReach.Located),
            [ResourcesLifecycle] = new(AccessLevel.Deploy, PermissionReach.Located),
            [ResourcesReadLogs] = new(AccessLevel.ReadSensitive, PermissionReach.Located),
            [TerminalsOpen] = new(AccessLevel.Root, PermissionReach.TeamAdmin),
            [SecretsRead] = new(AccessLevel.ReadSensitive, PermissionReach.Located),
            [SecretsWrite] = new(AccessLevel.Write, PermissionReach.Located),
            [SettingsManage] = new(AccessLevel.Write, PermissionReach.TeamAdmin),
            [TeamsManage] = new(AccessLevel.Root, PermissionReach.TeamAdmin),
            [NotificationsRead] = new(AccessLevel.Read, PermissionReach.TeamWide),
            [NotificationsWrite] = new(AccessLevel.Write, PermissionReach.TeamWide),
            [InstanceManage] = new(AccessLevel.Root, PermissionReach.InstanceAdmin)
        };

    public static readonly IReadOnlySet<string> All = Rules.Keys.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> TeamAdministrators = Rules
        .Where(rule => rule.Value.Reach != PermissionReach.InstanceAdmin)
        .Select(rule => rule.Key)
        .ToHashSet(StringComparer.Ordinal);

    // Coolify v4.2.0: a member without grants is read-only (team-wide Read).
    private static readonly IReadOnlySet<string> DefaultMember = Rules
        .Where(rule => rule.Value.Reach is PermissionReach.Located or PermissionReach.TeamWide
                       && rule.Value.Level == AccessLevel.Read)
        .Select(rule => rule.Key)
        .ToHashSet(StringComparer.Ordinal);

    public static PermissionRule? GetRule(string permission)
    {
        return Rules.TryGetValue(permission, out PermissionRule? rule) ? rule : null;
    }

    /// <summary>
    ///     Permissions a role holds team-wide <b>without</b> fine-grained grants. Owners and admins hold every team
    ///     permission; members default to read-only. Instance administration is never implied by a team role.
    /// </summary>
    public static IReadOnlySet<string> ForRole(TeamRole role)
    {
        return role switch
        {
            TeamRole.Owner or TeamRole.Admin => TeamAdministrators,
            TeamRole.Member => DefaultMember,
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
    }
}
