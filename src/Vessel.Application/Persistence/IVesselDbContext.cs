using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Notifications;
using Vessel.Domain.Projects;
using Vessel.Domain.Registries;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;
using Environment = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.Persistence;

public interface IVesselDbContext : IUnitOfWork
{
    IQueryable<User> Users { get; }

    IQueryable<Team> Teams { get; }

    IQueryable<TeamMembership> TeamMemberships { get; }

    IQueryable<TeamInvitation> TeamInvitations { get; }

    IQueryable<Project> Projects { get; }

    IQueryable<Environment> Environments { get; }

    IQueryable<Server> Servers { get; }

    IQueryable<AppEntity> Applications { get; }

    IQueryable<DatabaseResource> DatabaseResources { get; }

    IQueryable<Deployment> Deployments { get; }

    IQueryable<SecretReference> SecretReferences { get; }

    IQueryable<SecretValue> SecretValues { get; }

    IQueryable<EnvironmentVariable> EnvironmentVariables { get; }

    IQueryable<RegistryCredential> RegistryCredentials { get; }

    IQueryable<ServerStatusSnapshot> ServerStatusSnapshots { get; }

    IQueryable<NotificationTarget> NotificationTargets { get; }

    IQueryable<AuditLog> AuditLogs { get; }

    IQueryable<SettingEntry> Settings { get; }

    IQueryable<PersonalAccessToken> PersonalAccessTokens { get; }

    IRepository<User, UserId> UserRepository { get; }

    IRepository<Team, TeamId> TeamRepository { get; }

    IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository { get; }

    IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository { get; }

    IRepository<Project, ProjectId> ProjectRepository { get; }

    IRepository<Environment, EnvironmentId> EnvironmentRepository { get; }

    IRepository<Server, ServerId> ServerRepository { get; }

    IRepository<AppEntity, AppId> ApplicationRepository { get; }

    IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository { get; }

    IRepository<Deployment, DeploymentId> DeploymentRepository { get; }

    IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository { get; }

    IRepository<SecretValue, SecretValueId> SecretValueRepository { get; }

    IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository { get; }

    IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository { get; }

    IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository { get; }
}
