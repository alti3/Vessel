using System.Linq.Expressions;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Realtime;
using Vessel.Application.Security;
using Vessel.Application.Ssh;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Certificates;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Notifications;
using Vessel.Domain.Operations;
using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;
using Vessel.Domain.Projects;
using Vessel.Domain.Proxy;
using Vessel.Domain.Registries;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using Vessel.Domain.Settings;
using Vessel.Domain.Storage;
using Vessel.Domain.Teams;
using Vessel.Domain.Terminals;
using Vessel.Domain.Users;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Application.Backups;

/// <summary>List-backed persistence for backup, destination and managed-database service tests.</summary>
internal sealed class BackupTestDbContext : IVesselDbContext
{
    public List<User> UserItems { get; } = [];
    public List<Team> TeamItems { get; } = [];
    public List<Project> ProjectItems { get; } = [];
    public List<EnvironmentEntity> EnvironmentItems { get; } = [];
    public List<Server> ServerItems { get; } = [];
    public List<AppEntity> ApplicationItems { get; } = [];
    public List<Deployment> DeploymentItems { get; } = [];
    public List<TerminalSession> TerminalSessionItems { get; } = [];
    public List<ServerStatusSnapshot> ServerStatusSnapshotItems { get; } = [];
    public List<SecretValue> SecretValueItems { get; } = [];
    public List<DatabaseResource> DatabaseItems { get; } = [];
    public List<BackupSchedule> ScheduleItems { get; } = [];
    public List<BackupExecution> ExecutionItems { get; } = [];
    public List<DatabaseRestore> RestoreItems { get; } = [];
    public List<BackupDestination> DestinationItems { get; } = [];
    public List<SecretReference> SecretReferenceItems { get; } = [];
    public List<PersistentVolume> VolumeItems { get; } = [];
    public List<FileMount> FileMountItems { get; } = [];
    public List<ScheduledVolumeBackup> VolumeScheduleItems { get; } = [];
    public List<VolumeBackupExecution> VolumeExecutionItems { get; } = [];
    public List<VolumeBackupRestore> VolumeRestoreItems { get; } = [];
    public List<DatabaseImportUpload> ImportUploadItems { get; } = [];
    public int SaveCount { get; private set; }

    public IQueryable<PersistentVolume> PersistentVolumes => VolumeItems.AsQueryable();
    public IQueryable<FileMount> FileMounts => FileMountItems.AsQueryable();
    public IQueryable<ScheduledVolumeBackup> ScheduledVolumeBackups => VolumeScheduleItems.AsQueryable();
    public IQueryable<VolumeBackupExecution> VolumeBackupExecutions => VolumeExecutionItems.AsQueryable();
    public IQueryable<VolumeBackupRestore> VolumeBackupRestores => VolumeRestoreItems.AsQueryable();
    public IQueryable<DatabaseImportUpload> DatabaseImportUploads => ImportUploadItems.AsQueryable();
    public IRepository<PersistentVolume, PersistentVolumeId> PersistentVolumeRepository => new ListRepository<PersistentVolume, PersistentVolumeId>(VolumeItems);
    public IRepository<FileMount, FileMountId> FileMountRepository => new ListRepository<FileMount, FileMountId>(FileMountItems);
    public IRepository<ScheduledVolumeBackup, ScheduledVolumeBackupId> ScheduledVolumeBackupRepository => new ListRepository<ScheduledVolumeBackup, ScheduledVolumeBackupId>(VolumeScheduleItems);
    public IRepository<VolumeBackupExecution, VolumeBackupExecutionId> VolumeBackupExecutionRepository => new ListRepository<VolumeBackupExecution, VolumeBackupExecutionId>(VolumeExecutionItems);
    public IRepository<VolumeBackupRestore, VolumeBackupRestoreId> VolumeBackupRestoreRepository => new ListRepository<VolumeBackupRestore, VolumeBackupRestoreId>(VolumeRestoreItems);
    public IRepository<DatabaseImportUpload, DatabaseImportUploadId> DatabaseImportUploadRepository => new ListRepository<DatabaseImportUpload, DatabaseImportUploadId>(ImportUploadItems);
    public List<SettingEntry> SettingItems { get; } = [];
    public List<ServiceResource> ServiceItems { get; } = [];
    public List<EnvironmentVariable> VariableItems { get; } = [];
    public List<ScheduledJobRun> RunItems { get; } = [];
    public List<ScheduledTask> TaskItems { get; } = [];
    public List<ScheduledTaskExecution> TaskExecutionItems { get; } = [];
    public List<ControlPlaneBackup> ControlPlaneBackupItems { get; } = [];
    public List<UpgradeRun> UpgradeRunItems { get; } = [];

    /// <summary>Thrown by the next SaveChangesAsync (simulates a lost optimistic-concurrency race).</summary>
    public Exception? FailNextSave { get; set; }

    public IQueryable<ScheduledJobRun> ScheduledJobRuns => RunItems.AsQueryable();
    public IQueryable<ScheduledTask> ScheduledTasks => TaskItems.AsQueryable();
    public IQueryable<ScheduledTaskExecution> ScheduledTaskExecutions => TaskExecutionItems.AsQueryable();
    public IQueryable<ControlPlaneBackup> ControlPlaneBackups => ControlPlaneBackupItems.AsQueryable();
    public IQueryable<UpgradeRun> UpgradeRuns => UpgradeRunItems.AsQueryable();
    public IRepository<ScheduledJobRun, ScheduledJobRunId> ScheduledJobRunRepository => new ListRepository<ScheduledJobRun, ScheduledJobRunId>(RunItems);
    public IRepository<ScheduledTask, ScheduledTaskId> ScheduledTaskRepository => new ListRepository<ScheduledTask, ScheduledTaskId>(TaskItems);
    public IRepository<ScheduledTaskExecution, ScheduledTaskExecutionId> ScheduledTaskExecutionRepository => new ListRepository<ScheduledTaskExecution, ScheduledTaskExecutionId>(TaskExecutionItems);
    public IRepository<ControlPlaneBackup, ControlPlaneBackupId> ControlPlaneBackupRepository => new ListRepository<ControlPlaneBackup, ControlPlaneBackupId>(ControlPlaneBackupItems);
    public IRepository<UpgradeRun, UpgradeRunId> UpgradeRunRepository => new ListRepository<UpgradeRun, UpgradeRunId>(UpgradeRunItems);
    public IRepository<SettingEntry, SettingId> SettingRepository => new ListRepository<SettingEntry, SettingId>(SettingItems);

    public IQueryable<User> Users => UserItems.AsQueryable();
    public IQueryable<Team> Teams => TeamItems.AsQueryable();
    public IQueryable<TeamMembership> TeamMemberships => TeamItems.SelectMany(team => team.Memberships).AsQueryable();
    public IQueryable<TeamInvitation> TeamInvitations => Empty<TeamInvitation>();
    public IQueryable<Project> Projects => ProjectItems.AsQueryable();
    public IQueryable<EnvironmentEntity> Environments => EnvironmentItems.AsQueryable();
    public IQueryable<Server> Servers => ServerItems.AsQueryable();
    public IQueryable<AppEntity> Applications => ApplicationItems.AsQueryable();
    public IQueryable<ApplicationDomain> ApplicationDomains => ApplicationItems.SelectMany(application => application.Domains).AsQueryable();
    public IQueryable<DatabaseResource> DatabaseResources => DatabaseItems.AsQueryable();
    public IQueryable<ServiceResource> ServiceResources => ServiceItems.AsQueryable();
    public IQueryable<BackupSchedule> BackupSchedules => ScheduleItems.AsQueryable();
    public IQueryable<BackupExecution> BackupExecutions => ExecutionItems.AsQueryable();
    public IQueryable<DatabaseRestore> DatabaseRestores => RestoreItems.AsQueryable();
    public IQueryable<BackupDestination> BackupDestinations => DestinationItems.AsQueryable();
    public IQueryable<Deployment> Deployments => DeploymentItems.AsQueryable();
    public IQueryable<SecretReference> SecretReferences => SecretReferenceItems.AsQueryable();
    public IQueryable<SecretValue> SecretValues => SecretValueItems.AsQueryable();
    public IQueryable<EnvironmentVariable> EnvironmentVariables => VariableItems.AsQueryable();
    public IQueryable<RegistryCredential> RegistryCredentials => Empty<RegistryCredential>();
    public IQueryable<ServerStatusSnapshot> ServerStatusSnapshots => ServerStatusSnapshotItems.AsQueryable();
    public IQueryable<NotificationTarget> NotificationTargets => Empty<NotificationTarget>();
    public IQueryable<NotificationEvent> NotificationEvents => Empty<NotificationEvent>();
    public IQueryable<InAppNotification> InAppNotifications => Empty<InAppNotification>();
    public IQueryable<NotificationDeliveryAttempt> NotificationDeliveryAttempts => Empty<NotificationDeliveryAttempt>();
    public IQueryable<AuditLog> AuditLogs => Empty<AuditLog>();
    public IQueryable<SettingEntry> Settings => SettingItems.AsQueryable();
    public IQueryable<PersonalAccessToken> PersonalAccessTokens => Empty<PersonalAccessToken>();
    public IQueryable<WebhookEvent> WebhookEvents => Empty<WebhookEvent>();
    public IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations => Empty<ApplicationWebhookConfiguration>();
    public IQueryable<ApplicationPreview> ApplicationPreviews => Empty<ApplicationPreview>();
    public IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions => Empty<ProxyConfigurationVersion>();
    public IQueryable<Certificate> Certificates => Empty<Certificate>();
    public IQueryable<TerminalSession> TerminalSessions => TerminalSessionItems.AsQueryable();
    public IQueryable<ServerProxySettings> ServerProxySettings => Empty<ServerProxySettings>();

    public IRepository<User, UserId> UserRepository => new ListRepository<User, UserId>(UserItems);
    public IRepository<Team, TeamId> TeamRepository => new ListRepository<Team, TeamId>(TeamItems);
    public IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository => EmptyRepo<TeamInvitation, TeamInvitationId>();
    public IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository => EmptyRepo<PersonalAccessToken, PersonalAccessTokenId>();
    public IRepository<Project, ProjectId> ProjectRepository => new ListRepository<Project, ProjectId>(ProjectItems);
    public IRepository<EnvironmentEntity, EnvironmentId> EnvironmentRepository => new ListRepository<EnvironmentEntity, EnvironmentId>(EnvironmentItems);
    public IRepository<Server, ServerId> ServerRepository => new ListRepository<Server, ServerId>(ServerItems);
    public IRepository<AppEntity, Vessel.Domain.ApplicationId> ApplicationRepository => new ListRepository<AppEntity, Vessel.Domain.ApplicationId>(ApplicationItems);
    public IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository => new ListRepository<DatabaseResource, DatabaseResourceId>(DatabaseItems);
    public IRepository<ServiceResource, ServiceResourceId> ServiceResourceRepository => EmptyRepo<ServiceResource, ServiceResourceId>();
    public IRepository<BackupSchedule, BackupScheduleId> BackupScheduleRepository => new ListRepository<BackupSchedule, BackupScheduleId>(ScheduleItems);
    public IRepository<BackupExecution, BackupExecutionId> BackupExecutionRepository => new ListRepository<BackupExecution, BackupExecutionId>(ExecutionItems);
    public IRepository<DatabaseRestore, DatabaseRestoreId> DatabaseRestoreRepository => new ListRepository<DatabaseRestore, DatabaseRestoreId>(RestoreItems);
    public IRepository<BackupDestination, BackupDestinationId> BackupDestinationRepository => new ListRepository<BackupDestination, BackupDestinationId>(DestinationItems);
    public IRepository<Deployment, DeploymentId> DeploymentRepository => new ListRepository<Deployment, DeploymentId>(DeploymentItems);
    public IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository => new ListRepository<SecretReference, SecretReferenceId>(SecretReferenceItems);
    public IRepository<SecretValue, SecretValueId> SecretValueRepository => new ListRepository<SecretValue, SecretValueId>(SecretValueItems);
    public IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository => EmptyRepo<EnvironmentVariable, EnvironmentVariableId>();
    public IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository => EmptyRepo<RegistryCredential, RegistryCredentialId>();
    public IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository => new ListRepository<ServerStatusSnapshot, ServerStatusSnapshotId>(ServerStatusSnapshotItems);
    public IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository => EmptyRepo<WebhookEvent, WebhookEventId>();
    public IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId> ApplicationWebhookConfigurationRepository => EmptyRepo<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>();
    public IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository => EmptyRepo<ApplicationPreview, ApplicationPreviewId>();
    public IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId> ProxyConfigurationVersionRepository => EmptyRepo<ProxyConfigurationVersion, ProxyConfigurationVersionId>();
    public IRepository<Certificate, CertificateId> CertificateRepository => EmptyRepo<Certificate, CertificateId>();
    public IRepository<TerminalSession, TerminalSessionId> TerminalSessionRepository => new ListRepository<TerminalSession, TerminalSessionId>(TerminalSessionItems);
    public IRepository<ServerProxySettings, ServerId> ServerProxySettingsRepository => EmptyRepo<ServerProxySettings, ServerId>();

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSave is { } failure)
        {
            FailNextSave = null;
            throw failure;
        }

        SaveCount++;
        return Task.FromResult(1);
    }

    private static IQueryable<T> Empty<T>() => Array.Empty<T>().AsQueryable();

    private static IRepository<TEntity, TId> EmptyRepo<TEntity, TId>()
        where TEntity : Entity<TId>
        where TId : notnull => new EmptyRepository<TEntity, TId>();

    private sealed class ListRepository<TEntity, TId>(List<TEntity> items) : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.SingleOrDefault(item => EqualityComparer<TId>.Default.Equals(item.Id, id)));

        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            items.Add(entity);
            return Task.CompletedTask;
        }

        public void Remove(TEntity entity) => items.Remove(entity);
    }

    private sealed class EmptyRepository<TEntity, TId> : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default) => Task.FromResult<TEntity?>(null);
        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Remove(TEntity entity) => throw new NotSupportedException();
    }
}

