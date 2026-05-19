using Vessel.Domain;

namespace Vessel.Application.Dashboard;

public sealed class EmptyDashboardOverviewQuery : IDashboardOverviewQuery
{
    public DashboardOverview GetOverview(TeamId teamId)
    {
        return new DashboardOverview(0, 0, 0, 0, 0, 0, 0, []);
    }
}

public sealed class EmptyProjectCatalogQuery : IProjectCatalogQuery
{
    public IReadOnlyList<ProjectListItem> List(TeamId teamId) => [];
}

public sealed class EmptyServerCatalogQuery : IServerCatalogQuery
{
    public IReadOnlyList<ServerListItem> List(TeamId teamId) => [];
}

public sealed class EmptyApplicationCatalogQuery : IApplicationCatalogQuery
{
    public IReadOnlyList<ApplicationListItem> List(TeamId teamId) => [];
}

public sealed class EmptyDeploymentCatalogQuery : IDeploymentCatalogQuery
{
    public IReadOnlyList<DeploymentListItem> List(TeamId teamId) => [];
}

public sealed class EmptyDatabaseCatalogQuery : IDatabaseCatalogQuery
{
    public IReadOnlyList<DatabaseListItem> List(TeamId teamId) => [];
}

public sealed class EmptyNotificationCatalogQuery : INotificationCatalogQuery
{
    public IReadOnlyList<NotificationTargetListItem> List(TeamId teamId) => [];
}

public sealed class EmptySettingsCatalogQuery : ISettingsCatalogQuery
{
    public IReadOnlyList<SettingListItem> List(TeamId teamId) => [];
}
