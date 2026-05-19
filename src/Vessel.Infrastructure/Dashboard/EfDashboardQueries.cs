using Vessel.Application.Dashboard;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Deployments;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Infrastructure.Dashboard;

public sealed class EfDashboardQueries :
    IDashboardOverviewQuery,
    IProjectCatalogQuery,
    IServerCatalogQuery,
    IApplicationCatalogQuery,
    IDeploymentCatalogQuery,
    IDatabaseCatalogQuery,
    INotificationCatalogQuery,
    ISettingsCatalogQuery
{
    private readonly IVesselDbContext _dbContext;

    public EfDashboardQueries(IVesselDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public DashboardOverview GetOverview(TeamId teamId)
    {
        IReadOnlyList<DeploymentListItem> recentDeployments = ListDeployments(teamId, 5);

        return new DashboardOverview(
            _dbContext.Projects.Count(project => project.TeamId == teamId),
            _dbContext.Servers.Count(server => server.TeamId == teamId),
            ApplicationsForTeam(teamId).Count(),
            DatabasesForTeam(teamId).Count(),
            DeploymentsForTeam(teamId).Count(deployment => deployment.Status == DeploymentStatus.InProgress),
            DeploymentsForTeam(teamId).Count(deployment => deployment.Status == DeploymentStatus.Failed),
            _dbContext.NotificationTargets.Count(target => target.TeamId == teamId),
            recentDeployments);
    }

    public IReadOnlyList<ProjectListItem> List(TeamId teamId)
    {
        return _dbContext.Projects
            .Where(project => project.TeamId == teamId)
            .OrderBy(project => project.Name)
            .Select(project => new ProjectListItem(
                project.Id.Value,
                project.Name.Value,
                project.Description.ToString(),
                _dbContext.Environments.Count(environment => environment.ProjectId == project.Id)))
            .ToArray();
    }

    IReadOnlyList<ServerListItem> IServerCatalogQuery.List(TeamId teamId)
    {
        return _dbContext.Servers
            .Where(server => server.TeamId == teamId)
            .OrderBy(server => server.Name)
            .Select(server => new ServerListItem(
                server.Id.Value,
                server.Name.Value,
                server.Description.ToString(),
                server.Address.ToString(),
                server.Runtime,
                server.Status))
            .ToArray();
    }

    IReadOnlyList<ApplicationListItem> IApplicationCatalogQuery.List(TeamId teamId)
    {
        return ApplicationsForTeam(teamId)
            .OrderBy(application => application.Name)
            .Select(application => new ApplicationListItem(
                application.Id.Value,
                application.Name.Value,
                application.Description.ToString(),
                application.EnvironmentId.Value,
                application.ServerId.Value))
            .ToArray();
    }

    IReadOnlyList<DeploymentListItem> IDeploymentCatalogQuery.List(TeamId teamId)
    {
        return ListDeployments(teamId, 50);
    }

    IReadOnlyList<DatabaseListItem> IDatabaseCatalogQuery.List(TeamId teamId)
    {
        return DatabasesForTeam(teamId)
            .OrderBy(database => database.Name)
            .Select(database => new DatabaseListItem(
                database.Id.Value,
                database.Name.Value,
                database.Description.ToString(),
                database.Engine,
                database.Version.Value,
                database.HealthState,
                database.EnvironmentId.Value,
                database.ServerId.Value))
            .ToArray();
    }

    IReadOnlyList<NotificationTargetListItem> INotificationCatalogQuery.List(TeamId teamId)
    {
        return _dbContext.NotificationTargets
            .Where(target => target.TeamId == teamId)
            .OrderBy(target => target.Name)
            .Select(target => new NotificationTargetListItem(
                target.Id.Value,
                target.Name.Value,
                target.Channel))
            .ToArray();
    }

    IReadOnlyList<SettingListItem> ISettingsCatalogQuery.List(TeamId teamId)
    {
        return _dbContext.Settings
            .Where(setting => setting.TeamId == teamId || setting.TeamId == null)
            .OrderBy(setting => setting.Scope)
            .ThenBy(setting => setting.Key)
            .Select(setting => new SettingListItem(
                setting.Id.Value,
                setting.Scope.ToString(),
                setting.Key,
                setting.ResourceType))
            .ToArray();
    }

    private IReadOnlyList<DeploymentListItem> ListDeployments(TeamId teamId, int take)
    {
        return DeploymentsForTeam(teamId)
            .OrderByDescending(deployment => deployment.CreatedAt)
            .Take(take)
            .Select(deployment => new DeploymentListItem(
                deployment.Id.Value,
                deployment.ApplicationId.Value,
                deployment.ServerId.Value,
                deployment.Status,
                deployment.CommitSha,
                deployment.CreatedAt,
                deployment.FinishedAt))
            .ToArray();
    }

    private IQueryable<Domain.Applications.Application> ApplicationsForTeam(TeamId teamId)
    {
        IQueryable<EnvironmentEntity> teamEnvironments = _dbContext.Environments
            .Join(
                _dbContext.Projects.Where(project => project.TeamId == teamId),
                environment => environment.ProjectId,
                project => project.Id,
                (environment, _) => environment);

        return _dbContext.Applications
            .Join(
                teamEnvironments,
                application => application.EnvironmentId,
                environment => environment.Id,
                (application, _) => application);
    }

    private IQueryable<Domain.Databases.DatabaseResource> DatabasesForTeam(TeamId teamId)
    {
        IQueryable<EnvironmentEntity> teamEnvironments = _dbContext.Environments
            .Join(
                _dbContext.Projects.Where(project => project.TeamId == teamId),
                environment => environment.ProjectId,
                project => project.Id,
                (environment, _) => environment);

        return _dbContext.DatabaseResources
            .Join(
                teamEnvironments,
                database => database.EnvironmentId,
                environment => environment.Id,
                (database, _) => database);
    }

    private IQueryable<Deployment> DeploymentsForTeam(TeamId teamId)
    {
        return _dbContext.Deployments
            .Join(
                ApplicationsForTeam(teamId),
                deployment => deployment.ApplicationId,
                application => application.Id,
                (deployment, _) => deployment);
    }
}
