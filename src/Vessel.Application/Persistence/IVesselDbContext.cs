using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
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
using Vessel.Domain.Services;
using Vessel.Domain.Settings;
using Vessel.Domain.Teams;
using Vessel.Domain.Terminals;
using Vessel.Domain.Users;
using Vessel.Domain.Webhooks;
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

    IQueryable<ApplicationDomain> ApplicationDomains { get; }

    IQueryable<DatabaseResource> DatabaseResources { get; }

    IQueryable<ServiceResource> ServiceResources { get; }

    IQueryable<BackupSchedule> BackupSchedules { get; }

    IQueryable<BackupExecution> BackupExecutions { get; }

    IQueryable<Deployment> Deployments { get; }

    IQueryable<SecretReference> SecretReferences { get; }

    IQueryable<SecretValue> SecretValues { get; }

    IQueryable<EnvironmentVariable> EnvironmentVariables { get; }

    IQueryable<RegistryCredential> RegistryCredentials { get; }

    IQueryable<ServerStatusSnapshot> ServerStatusSnapshots { get; }

    IQueryable<NotificationTarget> NotificationTargets { get; }

    IQueryable<NotificationEvent> NotificationEvents { get; }

    IQueryable<InAppNotification> InAppNotifications { get; }

    IQueryable<NotificationDeliveryAttempt> NotificationDeliveryAttempts { get; }

    IQueryable<AuditLog> AuditLogs { get; }

    IQueryable<SettingEntry> Settings { get; }

    IQueryable<TerminalSession> TerminalSessions { get; }

    IQueryable<PersonalAccessToken> PersonalAccessTokens { get; }

    IQueryable<WebhookEvent> WebhookEvents { get; }

    IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations { get; }

    IQueryable<ApplicationPreview> ApplicationPreviews { get; }

    IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions { get; }

    IQueryable<Certificate> Certificates { get; }

    IRepository<User, UserId> UserRepository { get; }

    IRepository<Team, TeamId> TeamRepository { get; }

    IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository { get; }

    IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository { get; }

    IRepository<Project, ProjectId> ProjectRepository { get; }

    IRepository<Environment, EnvironmentId> EnvironmentRepository { get; }

    IRepository<Server, ServerId> ServerRepository { get; }

    IRepository<AppEntity, AppId> ApplicationRepository { get; }

    IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository { get; }

    IRepository<ServiceResource, ServiceResourceId> ServiceResourceRepository { get; }

    IRepository<BackupSchedule, BackupScheduleId> BackupScheduleRepository { get; }

    IRepository<BackupExecution, BackupExecutionId> BackupExecutionRepository { get; }

    IRepository<Deployment, DeploymentId> DeploymentRepository { get; }

    IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository { get; }

    IRepository<SecretValue, SecretValueId> SecretValueRepository { get; }

    IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository { get; }

    IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository { get; }

    IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository { get; }

    IRepository<NotificationTarget, NotificationTargetId> NotificationTargetRepository =>
        throw new InvalidOperationException("Notification target persistence is not available.");

    IRepository<NotificationEvent, NotificationEventId> NotificationEventRepository =>
        throw new InvalidOperationException("Notification event persistence is not available.");

    IRepository<InAppNotification, InAppNotificationId> InAppNotificationRepository =>
        throw new InvalidOperationException("In-app notification persistence is not available.");

    IRepository<NotificationDeliveryAttempt, NotificationDeliveryAttemptId> NotificationDeliveryAttemptRepository =>
        throw new InvalidOperationException("Notification delivery attempt persistence is not available.");

    IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository { get; }

    IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>
        ApplicationWebhookConfigurationRepository
    { get; }

    IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository { get; }

    IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId> ProxyConfigurationVersionRepository { get; }

    IRepository<Certificate, CertificateId> CertificateRepository { get; }

    IRepository<TerminalSession, TerminalSessionId> TerminalSessionRepository { get; }
}
