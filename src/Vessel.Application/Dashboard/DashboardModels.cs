using Vessel.Domain;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.Notifications;
using Vessel.Domain.Servers;

namespace Vessel.Application.Dashboard;

public interface IDashboardOverviewQuery
{
    DashboardOverview GetOverview(TeamId teamId);
}

public interface IProjectCatalogQuery
{
    IReadOnlyList<ProjectListItem> List(TeamId teamId);
}

public interface IServerCatalogQuery
{
    IReadOnlyList<ServerListItem> List(TeamId teamId);
}

public interface IApplicationCatalogQuery
{
    IReadOnlyList<ApplicationListItem> List(TeamId teamId);
}

public interface IDeploymentCatalogQuery
{
    IReadOnlyList<DeploymentListItem> List(TeamId teamId);
}

public interface IDatabaseCatalogQuery
{
    IReadOnlyList<DatabaseListItem> List(TeamId teamId);
}

public interface INotificationCatalogQuery
{
    IReadOnlyList<NotificationTargetListItem> List(TeamId teamId);
}

public interface ISettingsCatalogQuery
{
    IReadOnlyList<SettingListItem> List(TeamId teamId);
}

public sealed record DashboardOverview(
    int ProjectCount,
    int ServerCount,
    int ApplicationCount,
    int DatabaseCount,
    int ActiveDeploymentCount,
    int FailedDeploymentCount,
    int NotificationTargetCount,
    IReadOnlyList<DeploymentListItem> RecentDeployments);

public sealed record ProjectListItem(
    Guid Id,
    string Name,
    string? Description,
    int EnvironmentCount);

public sealed record ServerListItem(
    Guid Id,
    string Name,
    string? Description,
    string Address,
    ContainerRuntimeKind Runtime,
    ServerStatus Status);

public sealed record ApplicationListItem(
    Guid Id,
    string Name,
    string? Description,
    Guid EnvironmentId,
    Guid ServerId);

public sealed record DeploymentListItem(
    Guid Id,
    Guid ApplicationId,
    Guid ServerId,
    DeploymentStatus Status,
    string? CommitSha,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record DatabaseListItem(
    Guid Id,
    string Name,
    string? Description,
    DatabaseEngine Engine,
    string Version,
    DatabaseHealthState HealthState,
    Guid EnvironmentId,
    Guid ServerId);

public sealed record NotificationTargetListItem(
    Guid Id,
    string Name,
    NotificationChannel Channel);

public sealed record SettingListItem(
    Guid Id,
    string Scope,
    string Key,
    string? ResourceType);
