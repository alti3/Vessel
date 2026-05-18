using Vessel.Domain.Teams;

namespace Vessel.Application.Authorization;

public static class VesselPermissions
{
    public const string ProjectsRead = "projects.read";
    public const string ProjectsWrite = "projects.write";
    public const string ServersRead = "servers.read";
    public const string ServersWrite = "servers.write";
    public const string ApplicationsRead = "applications.read";
    public const string ApplicationsWrite = "applications.write";
    public const string DeploymentsStart = "deployments.start";
    public const string DeploymentsCancel = "deployments.cancel";
    public const string DeploymentsReadLogs = "deployments.readLogs";
    public const string TerminalsOpen = "terminals.open";
    public const string SecretsRead = "secrets.read";
    public const string SecretsWrite = "secrets.write";
    public const string SettingsManage = "settings.manage";
    public const string TeamsManage = "teams.manage";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ProjectsRead,
        ProjectsWrite,
        ServersRead,
        ServersWrite,
        ApplicationsRead,
        ApplicationsWrite,
        DeploymentsStart,
        DeploymentsCancel,
        DeploymentsReadLogs,
        TerminalsOpen,
        SecretsRead,
        SecretsWrite,
        SettingsManage,
        TeamsManage
    };

    public static IReadOnlySet<string> ForRole(TeamRole role)
    {
        return role switch
        {
            TeamRole.Owner or TeamRole.Admin => All,
            TeamRole.Member => new HashSet<string>(StringComparer.Ordinal)
            {
                ProjectsRead,
                ServersRead,
                ApplicationsRead,
                DeploymentsStart,
                DeploymentsCancel,
                DeploymentsReadLogs
            },
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
    }
}
