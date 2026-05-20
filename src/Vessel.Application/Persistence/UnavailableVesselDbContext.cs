using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Notifications;
using Vessel.Domain.Projects;
using Vessel.Domain.Proxy;
using Vessel.Domain.Registries;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.Persistence;

public sealed class UnavailableVesselDbContext : IVesselDbContext
{
    public IQueryable<User> Users => Enumerable.Empty<User>().AsQueryable();
    public IQueryable<Team> Teams => Enumerable.Empty<Team>().AsQueryable();
    public IQueryable<TeamMembership> TeamMemberships => Enumerable.Empty<TeamMembership>().AsQueryable();
    public IQueryable<TeamInvitation> TeamInvitations => Enumerable.Empty<TeamInvitation>().AsQueryable();
    public IQueryable<Project> Projects => Enumerable.Empty<Project>().AsQueryable();
    public IQueryable<EnvironmentEntity> Environments => Enumerable.Empty<EnvironmentEntity>().AsQueryable();
    public IQueryable<Server> Servers => Enumerable.Empty<Server>().AsQueryable();
    public IQueryable<AppEntity> Applications => Enumerable.Empty<AppEntity>().AsQueryable();
    public IQueryable<ApplicationDomain> ApplicationDomains => Enumerable.Empty<ApplicationDomain>().AsQueryable();
    public IQueryable<DatabaseResource> DatabaseResources => Enumerable.Empty<DatabaseResource>().AsQueryable();
    public IQueryable<Deployment> Deployments => Enumerable.Empty<Deployment>().AsQueryable();
    public IQueryable<SecretReference> SecretReferences => Enumerable.Empty<SecretReference>().AsQueryable();
    public IQueryable<SecretValue> SecretValues => Enumerable.Empty<SecretValue>().AsQueryable();
    public IQueryable<EnvironmentVariable> EnvironmentVariables => Enumerable.Empty<EnvironmentVariable>().AsQueryable();
    public IQueryable<RegistryCredential> RegistryCredentials => Enumerable.Empty<RegistryCredential>().AsQueryable();
    public IQueryable<ServerStatusSnapshot> ServerStatusSnapshots => Enumerable.Empty<ServerStatusSnapshot>().AsQueryable();
    public IQueryable<NotificationTarget> NotificationTargets => Enumerable.Empty<NotificationTarget>().AsQueryable();
    public IQueryable<AuditLog> AuditLogs => Enumerable.Empty<AuditLog>().AsQueryable();
    public IQueryable<SettingEntry> Settings => Enumerable.Empty<SettingEntry>().AsQueryable();
    public IQueryable<PersonalAccessToken> PersonalAccessTokens => Enumerable.Empty<PersonalAccessToken>().AsQueryable();
    public IQueryable<WebhookEvent> WebhookEvents => Enumerable.Empty<WebhookEvent>().AsQueryable();
    public IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations => Enumerable.Empty<ApplicationWebhookConfiguration>().AsQueryable();
    public IQueryable<ApplicationPreview> ApplicationPreviews => Enumerable.Empty<ApplicationPreview>().AsQueryable();
    public IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions => Enumerable.Empty<ProxyConfigurationVersion>().AsQueryable();
    public IQueryable<Certificate> Certificates => Enumerable.Empty<Certificate>().AsQueryable();

    public IRepository<User, UserId> UserRepository { get; } = new UnavailableRepository<User, UserId>();
    public IRepository<Team, TeamId> TeamRepository { get; } = new UnavailableRepository<Team, TeamId>();
    public IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository { get; } = new UnavailableRepository<TeamInvitation, TeamInvitationId>();
    public IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository { get; } = new UnavailableRepository<PersonalAccessToken, PersonalAccessTokenId>();
    public IRepository<Project, ProjectId> ProjectRepository { get; } = new UnavailableRepository<Project, ProjectId>();
    public IRepository<EnvironmentEntity, EnvironmentId> EnvironmentRepository { get; } = new UnavailableRepository<EnvironmentEntity, EnvironmentId>();
    public IRepository<Server, ServerId> ServerRepository { get; } = new UnavailableRepository<Server, ServerId>();
    public IRepository<AppEntity, AppId> ApplicationRepository { get; } = new UnavailableRepository<AppEntity, AppId>();
    public IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository { get; } = new UnavailableRepository<DatabaseResource, DatabaseResourceId>();
    public IRepository<Deployment, DeploymentId> DeploymentRepository { get; } = new UnavailableRepository<Deployment, DeploymentId>();
    public IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository { get; } = new UnavailableRepository<SecretReference, SecretReferenceId>();
    public IRepository<SecretValue, SecretValueId> SecretValueRepository { get; } = new UnavailableRepository<SecretValue, SecretValueId>();
    public IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository { get; } = new UnavailableRepository<EnvironmentVariable, EnvironmentVariableId>();
    public IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository { get; } = new UnavailableRepository<RegistryCredential, RegistryCredentialId>();
    public IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository { get; } = new UnavailableRepository<ServerStatusSnapshot, ServerStatusSnapshotId>();
    public IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository { get; } = new UnavailableRepository<WebhookEvent, WebhookEventId>();
    public IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId> ApplicationWebhookConfigurationRepository { get; } = new UnavailableRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>();
    public IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository { get; } = new UnavailableRepository<ApplicationPreview, ApplicationPreviewId>();
    public IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId> ProxyConfigurationVersionRepository { get; } = new UnavailableRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId>();
    public IRepository<Certificate, CertificateId> CertificateRepository { get; } = new UnavailableRepository<Certificate, CertificateId>();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Vessel database persistence is disabled.");
    }

    private sealed class UnavailableRepository<TEntity, TId> : IRepository<TEntity, TId>
        where TEntity : Domain.Common.Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TEntity?>(null);
        }

        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Vessel database persistence is disabled.");
        }

        public void Remove(TEntity entity)
        {
            throw new InvalidOperationException("Vessel database persistence is disabled.");
        }
    }
}
