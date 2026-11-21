using Microsoft.EntityFrameworkCore;
using Vessel.Application.Persistence;
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
using Vessel.Domain.Ssh;
using Vessel.Domain.Teams;
using Vessel.Domain.Terminals;
using Vessel.Domain.Users;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Infrastructure.Persistence;

public sealed partial class VesselDbContext : DbContext, IVesselDbContext
{
    public VesselDbContext(DbContextOptions<VesselDbContext> options)
        : base(options)
    {
        UserRepository = new EfRepository<User, UserId>(this);
        PersonalAccessTokenRepository = new EfRepository<PersonalAccessToken, PersonalAccessTokenId>(this);
        TeamRepository = new EfRepository<Team, TeamId>(this);
        TeamInvitationRepository = new EfRepository<TeamInvitation, TeamInvitationId>(this);
        ProjectRepository = new EfRepository<Project, ProjectId>(this);
        EnvironmentRepository = new EfRepository<EnvironmentEntity, EnvironmentId>(this);
        ServerRepository = new EfRepository<Server, ServerId>(this);
        ApplicationRepository = new EfRepository<AppEntity, AppId>(this);
        DatabaseResourceRepository = new EfRepository<DatabaseResource, DatabaseResourceId>(this);
        ServiceResourceRepository = new EfRepository<ServiceResource, ServiceResourceId>(this);
        BackupScheduleRepository = new EfRepository<BackupSchedule, BackupScheduleId>(this);
        BackupExecutionRepository = new EfRepository<BackupExecution, BackupExecutionId>(this);
        DatabaseRestoreRepository = new EfRepository<DatabaseRestore, DatabaseRestoreId>(this);
        BackupDestinationRepository = new EfRepository<BackupDestination, BackupDestinationId>(this);
        DeploymentRepository = new EfRepository<Deployment, DeploymentId>(this);
        TerminalSessionRepository = new EfRepository<TerminalSession, TerminalSessionId>(this);
        SecretReferenceRepository = new EfRepository<SecretReference, SecretReferenceId>(this);
        SecretValueRepository = new EfRepository<SecretValue, SecretValueId>(this);
        EnvironmentVariableRepository = new EfRepository<EnvironmentVariable, EnvironmentVariableId>(this);
        RegistryCredentialRepository = new EfRepository<RegistryCredential, RegistryCredentialId>(this);
        ServerStatusSnapshotRepository = new EfRepository<ServerStatusSnapshot, ServerStatusSnapshotId>(this);
        NotificationTargetRepository = new EfRepository<NotificationTarget, NotificationTargetId>(this);
        NotificationEventRepository = new EfRepository<NotificationEvent, NotificationEventId>(this);
        InAppNotificationRepository = new EfRepository<InAppNotification, InAppNotificationId>(this);
        NotificationDeliveryAttemptRepository =
            new EfRepository<NotificationDeliveryAttempt, NotificationDeliveryAttemptId>(this);
        InstanceEmailSettingsRepository = new EfRepository<InstanceEmailSettings, InstanceEmailSettingsId>(this);
        WebhookEventRepository = new EfRepository<WebhookEvent, WebhookEventId>(this);
        ApplicationWebhookConfigurationRepository =
            new EfRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>(this);
        ApplicationPreviewRepository = new EfRepository<ApplicationPreview, ApplicationPreviewId>(this);
        ProxyConfigurationVersionRepository =
            new EfRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId>(this);
        CertificateRepository = new EfRepository<Certificate, CertificateId>(this);
        ServerProxySettingsRepository = new EfRepository<ServerProxySettings, ServerId>(this);
        SshKeyRepository = new EfRepository<SshKey, SshKeyId>(this);

        SettingRepository = new EfRepository<SettingEntry, SettingId>(this);
    }

    public DbSet<User> UserSet => Set<User>();

    public DbSet<PersonalAccessToken> PersonalAccessTokenSet => Set<PersonalAccessToken>();

    public DbSet<Team> TeamSet => Set<Team>();

    public DbSet<TeamMembership> TeamMembershipSet => Set<TeamMembership>();

    public DbSet<TeamInvitation> TeamInvitationSet => Set<TeamInvitation>();

    public DbSet<Project> ProjectSet => Set<Project>();

    public DbSet<EnvironmentEntity> EnvironmentSet => Set<EnvironmentEntity>();

    public DbSet<Server> ServerSet => Set<Server>();

    public DbSet<AppEntity> ApplicationSet => Set<AppEntity>();

    public DbSet<ApplicationDomain> ApplicationDomainSet => Set<ApplicationDomain>();

    public DbSet<DatabaseResource> DatabaseResourceSet => Set<DatabaseResource>();

    public DbSet<ServiceResource> ServiceResourceSet => Set<ServiceResource>();

    public DbSet<BackupSchedule> BackupScheduleSet => Set<BackupSchedule>();

    public DbSet<BackupExecution> BackupExecutionSet => Set<BackupExecution>();

    public DbSet<DatabaseRestore> DatabaseRestoreSet => Set<DatabaseRestore>();

    public DbSet<BackupDestination> BackupDestinationSet => Set<BackupDestination>();

    public DbSet<Deployment> DeploymentSet => Set<Deployment>();

    public DbSet<TerminalSession> TerminalSessionSet => Set<TerminalSession>();

    public DbSet<SecretReference> SecretReferenceSet => Set<SecretReference>();

    public DbSet<SecretValue> SecretValueSet => Set<SecretValue>();

    public DbSet<EnvironmentVariable> EnvironmentVariableSet => Set<EnvironmentVariable>();

    public DbSet<RegistryCredential> RegistryCredentialSet => Set<RegistryCredential>();

    public DbSet<ServerStatusSnapshot> ServerStatusSnapshotSet => Set<ServerStatusSnapshot>();

    public DbSet<NotificationTarget> NotificationTargetSet => Set<NotificationTarget>();

    public DbSet<NotificationEvent> NotificationEventSet => Set<NotificationEvent>();

    public DbSet<InAppNotification> InAppNotificationSet => Set<InAppNotification>();

    public DbSet<NotificationDeliveryAttempt> NotificationDeliveryAttemptSet => Set<NotificationDeliveryAttempt>();

    public DbSet<AuditLog> AuditLogSet => Set<AuditLog>();

    public DbSet<SettingEntry> SettingSet => Set<SettingEntry>();

    public DbSet<WebhookEvent> WebhookEventSet => Set<WebhookEvent>();

    public DbSet<ApplicationWebhookConfiguration> ApplicationWebhookConfigurationSet =>
        Set<ApplicationWebhookConfiguration>();

    public DbSet<ApplicationPreview> ApplicationPreviewSet => Set<ApplicationPreview>();

    public DbSet<ProxyConfigurationVersion> ProxyConfigurationVersionSet => Set<ProxyConfigurationVersion>();

    public DbSet<Certificate> CertificateSet => Set<Certificate>();

    public DbSet<ServerProxySettings> ServerProxySettingsSet => Set<ServerProxySettings>();

    public DbSet<SshKey> SshKeySet => Set<SshKey>();

    public IQueryable<User> Users => UserSet;

    public IQueryable<PersonalAccessToken> PersonalAccessTokens => PersonalAccessTokenSet;

    public IQueryable<Team> Teams => TeamSet;

    public IQueryable<TeamMembership> TeamMemberships => TeamMembershipSet;

    public IQueryable<TeamInvitation> TeamInvitations => TeamInvitationSet;

    public IQueryable<Project> Projects => ProjectSet;

    public IQueryable<EnvironmentEntity> Environments => EnvironmentSet;

    public IQueryable<Server> Servers => ServerSet;

    public IQueryable<AppEntity> Applications => ApplicationSet;

    public IQueryable<ApplicationDomain> ApplicationDomains => ApplicationDomainSet;

    public IQueryable<DatabaseResource> DatabaseResources => DatabaseResourceSet;

    public IQueryable<ServiceResource> ServiceResources => ServiceResourceSet;

    public IQueryable<BackupSchedule> BackupSchedules => BackupScheduleSet;

    public IQueryable<BackupExecution> BackupExecutions => BackupExecutionSet;

    public IQueryable<DatabaseRestore> DatabaseRestores => DatabaseRestoreSet;

    public IQueryable<BackupDestination> BackupDestinations => BackupDestinationSet;

    public IQueryable<Deployment> Deployments => DeploymentSet;

    public IQueryable<TerminalSession> TerminalSessions => TerminalSessionSet;

    public IQueryable<SecretReference> SecretReferences => SecretReferenceSet;

    public IQueryable<SecretValue> SecretValues => SecretValueSet;

    public IQueryable<EnvironmentVariable> EnvironmentVariables => EnvironmentVariableSet;

    public IQueryable<RegistryCredential> RegistryCredentials => RegistryCredentialSet;

    public IQueryable<ServerStatusSnapshot> ServerStatusSnapshots => ServerStatusSnapshotSet;

    public IQueryable<NotificationTarget> NotificationTargets => NotificationTargetSet;

    public IQueryable<NotificationEvent> NotificationEvents => NotificationEventSet;

    public IQueryable<InAppNotification> InAppNotifications => InAppNotificationSet;

    public IQueryable<NotificationDeliveryAttempt> NotificationDeliveryAttempts => NotificationDeliveryAttemptSet;

    public IQueryable<InstanceEmailSettings> InstanceEmailSettings => Set<InstanceEmailSettings>();

    public IQueryable<AuditLog> AuditLogs => AuditLogSet;

    public IQueryable<SettingEntry> Settings => SettingSet;

    public IQueryable<WebhookEvent> WebhookEvents => WebhookEventSet;

    public IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations =>
        ApplicationWebhookConfigurationSet;

    public IQueryable<ApplicationPreview> ApplicationPreviews => ApplicationPreviewSet;

    public IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions => ProxyConfigurationVersionSet;

    public IQueryable<Certificate> Certificates => CertificateSet;

    public IQueryable<ServerProxySettings> ServerProxySettings => ServerProxySettingsSet;

    public IQueryable<SshKey> SshKeys => SshKeySet;

    public IRepository<User, UserId> UserRepository { get; }

    public IRepository<PersonalAccessToken, PersonalAccessTokenId> PersonalAccessTokenRepository { get; }

    public IRepository<Team, TeamId> TeamRepository { get; }

    public IRepository<TeamInvitation, TeamInvitationId> TeamInvitationRepository { get; }

    public IRepository<Project, ProjectId> ProjectRepository { get; }

    public IRepository<EnvironmentEntity, EnvironmentId> EnvironmentRepository { get; }

    public IRepository<Server, ServerId> ServerRepository { get; }

    public IRepository<AppEntity, AppId> ApplicationRepository { get; }

    public IRepository<DatabaseResource, DatabaseResourceId> DatabaseResourceRepository { get; }

    public IRepository<ServiceResource, ServiceResourceId> ServiceResourceRepository { get; }

    public IRepository<BackupSchedule, BackupScheduleId> BackupScheduleRepository { get; }

    public IRepository<BackupExecution, BackupExecutionId> BackupExecutionRepository { get; }

    public IRepository<DatabaseRestore, DatabaseRestoreId> DatabaseRestoreRepository { get; }

    public IRepository<BackupDestination, BackupDestinationId> BackupDestinationRepository { get; }

    public IRepository<Deployment, DeploymentId> DeploymentRepository { get; }

    public IRepository<TerminalSession, TerminalSessionId> TerminalSessionRepository { get; }

    public IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository { get; }

    public IRepository<SecretValue, SecretValueId> SecretValueRepository { get; }

    public IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository { get; }

    public IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository { get; }

    public IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository { get; }

    public IRepository<NotificationTarget, NotificationTargetId> NotificationTargetRepository { get; }

    public IRepository<NotificationEvent, NotificationEventId> NotificationEventRepository { get; }

    public IRepository<InAppNotification, InAppNotificationId> InAppNotificationRepository { get; }

    public IRepository<NotificationDeliveryAttempt, NotificationDeliveryAttemptId> NotificationDeliveryAttemptRepository
    {
        get;
    }

    public IRepository<InstanceEmailSettings, InstanceEmailSettingsId> InstanceEmailSettingsRepository { get; }

    public IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository { get; }

    public IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>
        ApplicationWebhookConfigurationRepository
    { get; }

    public IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository { get; }

    public IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId> ProxyConfigurationVersionRepository
    {
        get;
    }

    public IRepository<Certificate, CertificateId> CertificateRepository { get; }

    public IRepository<ServerProxySettings, ServerId> ServerProxySettingsRepository { get; }

    public IRepository<SshKey, SshKeyId> SshKeyRepository { get; }

    public IRepository<SettingEntry, SettingId> SettingRepository { get; }

    public async Task RefreshAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry = Entry(entity);
        if (entry.State is EntityState.Detached or EntityState.Added) return;
        await entry.ReloadAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("vessel");

        ConfigureUsers(modelBuilder);
        ConfigureTeams(modelBuilder);
        ConfigureProjects(modelBuilder);
        ConfigureServers(modelBuilder);
        ConfigureSshKeys(modelBuilder);
        ConfigureApplications(modelBuilder);
        ConfigureDatabases(modelBuilder);
        ConfigureManagedServices(modelBuilder);
        ConfigureBackups(modelBuilder);
        ConfigureDeployments(modelBuilder);
        ConfigureTerminals(modelBuilder);
        ConfigureSecrets(modelBuilder);
        ConfigureEnvironmentVariables(modelBuilder);
        ConfigureRegistryCredentials(modelBuilder);
        ConfigureNotifications(modelBuilder);
        NotificationEmailModelConfiguration.Configure(modelBuilder);
        ConfigureAuditLogs(modelBuilder);
        ConfigureSettings(modelBuilder);
        ConfigureWebhooks(modelBuilder);
        ConfigureProxy(modelBuilder);
        ConfigureRouting(modelBuilder);
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(builder =>
        {
            builder.ToTable("users");
            builder.HasKey(user => user.Id);
            builder.Property(user => user.Id).HasUserIdConversion();
            builder.Property(user => user.Name).HasConversion(ValueObjectConversions.DisplayName).HasMaxLength(120)
                .IsRequired();
            builder.Property(user => user.Email).HasConversion(ValueObjectConversions.EmailAddress).HasMaxLength(320)
                .IsRequired();
            builder.Property(user => user.ExternalSubject).HasMaxLength(255);
            builder.Property(user => user.PasswordHash).HasMaxLength(512);
            builder.Property(user => user.PasswordResetTokenHash).HasMaxLength(128);
            builder.Property(user => user.TwoFactorSecret).HasMaxLength(User.TwoFactorSecretMaxLength);
            builder.Property(user => user.IsInstanceAdmin).IsRequired();
            builder.Property(user => user.TwoFactorRecoveryCodeHashes).HasMaxLength(4000);
            builder.Property(user => user.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(user => user.DomainEvents);
            builder.HasIndex(user => user.Email).IsUnique();
        });

        modelBuilder.Entity<PersonalAccessToken>(builder =>
        {
            builder.ToTable("personal_access_tokens");
            builder.HasKey(token => token.Id);
            builder.Property(token => token.Id).HasPersonalAccessTokenIdConversion();
            builder.Property(token => token.UserId).HasUserIdConversion();
            builder.Property(token => token.TeamId).HasTeamIdConversion();
            builder.Property(token => token.Name).HasMaxLength(255).IsRequired();
            builder.Property(token => token.TokenHash).HasMaxLength(128).IsRequired();
            builder.Property(token => token.Scopes).HasMaxLength(512).IsRequired();
            builder.Property(token => token.AccessGrants).HasAccessGrantsConversion();
            builder.Property(token => token.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(token => token.DomainEvents);
            builder.HasIndex(token => token.TokenHash).IsUnique();
            builder.HasIndex(token => new { token.UserId, token.CreatedAt });
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(token => token.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(token => token.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureTeams(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(builder =>
        {
            builder.ToTable("teams");
            builder.HasKey(team => team.Id);
            builder.Property(team => team.Id).HasTeamIdConversion();
            builder.Property(team => team.Name).HasConversion(ValueObjectConversions.DisplayName).HasMaxLength(120)
                .IsRequired();
            builder.Property(team => team.Description).HasConversion(ValueObjectConversions.NullableDescription)
                .HasMaxLength(1000);
            builder.Property(team => team.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(team => team.DomainEvents);
            builder.HasMany(team => team.Memberships)
                .WithOne()
                .HasForeignKey(membership => membership.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Navigation(team => team.Memberships).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<TeamMembership>(builder =>
        {
            builder.ToTable("team_memberships");
            builder.HasKey(membership => new { membership.TeamId, membership.UserId });
            builder.Property(membership => membership.TeamId).HasTeamIdConversion();
            builder.Property(membership => membership.UserId).HasUserIdConversion();
            builder.Property(membership => membership.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(membership => membership.AccessGrants).HasAccessGrantsConversion();
            builder.Property(membership => membership.ConcurrencyStamp).IsConcurrencyToken();
            builder.HasIndex(membership => membership.UserId);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(membership => membership.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamInvitation>(builder =>
        {
            builder.ToTable("team_invitations");
            builder.HasKey(invitation => invitation.Id);
            builder.Property(invitation => invitation.Id).HasTeamInvitationIdConversion();
            builder.Property(invitation => invitation.TeamId).HasTeamIdConversion();
            builder.Property(invitation => invitation.Email).HasConversion(ValueObjectConversions.EmailAddress)
                .HasMaxLength(320).IsRequired();
            builder.Property(invitation => invitation.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(invitation => invitation.TokenHash).HasMaxLength(128).IsRequired();
            builder.Property(invitation => invitation.AccessGrants).HasAccessGrantsConversion();
            builder.Property(invitation => invitation.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(invitation => invitation.DomainEvents);
            builder.HasIndex(invitation => invitation.TokenHash).IsUnique();
            builder.HasIndex(invitation => new { invitation.TeamId, invitation.Email });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(invitation => invitation.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureProjects(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(builder =>
        {
            builder.ToTable("projects");
            builder.HasKey(project => project.Id);
            builder.Property(project => project.Id).HasProjectIdConversion();
            builder.Property(project => project.TeamId).HasTeamIdConversion();
            builder.Property(project => project.Name).HasConversion(ValueObjectConversions.ResourceName)
                .HasMaxLength(120).IsRequired();
            builder.Property(project => project.Description).HasConversion(ValueObjectConversions.NullableDescription)
                .HasMaxLength(1000);
            builder.Property(project => project.IsArchived).IsRequired();
            builder.Property(project => project.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(project => project.DomainEvents);
            builder.HasIndex(project => new { project.TeamId, project.Name }).IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(project => project.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EnvironmentEntity>(builder =>
        {
            builder.ToTable("environments");
            builder.HasKey(environment => environment.Id);
            builder.Property(environment => environment.Id).HasEnvironmentIdConversion();
            builder.Property(environment => environment.ProjectId).HasProjectIdConversion();
            builder.Property(environment => environment.Name).HasConversion(ValueObjectConversions.Slug)
                .HasMaxLength(80).IsRequired();
            builder.Property(environment => environment.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(environment => environment.Description)
                .HasConversion(ValueObjectConversions.NullableDescription).HasMaxLength(1000);
            builder.Property(environment => environment.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(environment => environment.DomainEvents);
            builder.HasIndex(environment => new { environment.ProjectId, environment.Name }).IsUnique();
            builder.HasOne<Project>()
                .WithMany()
                .HasForeignKey(environment => environment.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureServers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Server>(builder =>
        {
            builder.ToTable("servers");
            builder.HasKey(server => server.Id);
            builder.Property(server => server.Id).HasServerIdConversion();
            builder.Property(server => server.TeamId).HasTeamIdConversion();
            builder.Property(server => server.Name).HasConversion(ValueObjectConversions.ResourceName).HasMaxLength(120)
                .IsRequired();
            builder.Property(server => server.Description).HasConversion(ValueObjectConversions.NullableDescription)
                .HasMaxLength(1000);
            builder.Property(server => server.Address).HasConversion(ValueObjectConversions.ServerAddress)
                .HasMaxLength(512).IsRequired();
            builder.Property(server => server.ConnectionType).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(server => server.Runtime).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(server => server.Capabilities).HasConversion<int>().IsRequired();
            builder.Property(server => server.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(server => server.Labels).HasMaxLength(1000).IsRequired();
            builder.Property(server => server.SshKeyId).HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new SshKeyId(value.Value) : null);
            builder.Property(server => server.HostKeyChecking).HasConversion<string>().HasMaxLength(16)
                .HasDefaultValue(SshHostKeyCheckingMode.Enforce).HasSentinel((SshHostKeyCheckingMode)(-1)).IsRequired();
            builder.Property(server => server.PinnedHostKey).HasMaxLength(Server.MaxHostKeyLength);
            builder.Property(server => server.PendingHostKey).HasMaxLength(Server.MaxHostKeyLength);
            builder.Property(server => server.TerminalAccessEnabled).HasDefaultValue(true).HasSentinel(true)
                .IsRequired();
            builder.OwnsOne(server => server.Validation, validation =>
            {
                validation.Property(item => item.Succeeded).HasColumnName("validation_succeeded");
                validation.Property(item => item.OsId).HasColumnName("validation_os_id")
                    .HasMaxLength(ServerValidation.MaxFieldLength);
                validation.Property(item => item.OsIdLike).HasColumnName("validation_os_id_like")
                    .HasMaxLength(ServerValidation.MaxFieldLength);
                validation.Property(item => item.OsVersion).HasColumnName("validation_os_version")
                    .HasMaxLength(ServerValidation.MaxFieldLength);
                validation.Property(item => item.PackageManager).HasColumnName("validation_package_manager")
                    .HasMaxLength(ServerValidation.MaxFieldLength);
                validation.Property(item => item.RuntimeVersion).HasColumnName("validation_runtime_version")
                    .HasMaxLength(ServerValidation.MaxFieldLength);
                validation.Property(item => item.ComposeVersion).HasColumnName("validation_compose_version")
                    .HasMaxLength(ServerValidation.MaxFieldLength);
                validation.Property(item => item.IsRootUser).HasColumnName("validation_is_root_user");
                validation.Property(item => item.HasPasswordlessSudo).HasColumnName("validation_has_passwordless_sudo");
                validation.Property(item => item.Message).HasColumnName("validation_message")
                    .HasMaxLength(ServerValidation.MaxMessageLength);
                validation.Property(item => item.ValidatedAt).HasColumnName("validation_validated_at");
            });
            builder.HasIndex(server => server.SshKeyId);
            builder.HasOne<SshKey>()
                .WithMany()
                .HasForeignKey(server => server.SshKeyId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Property(server => server.ConcurrentBuilds).HasDefaultValue(Server.DefaultConcurrentBuilds)
                .IsRequired();
            builder.Property(server => server.DeploymentQueueLimit)
                .HasDefaultValue(Server.DefaultDeploymentQueueLimit).IsRequired();
            builder.Property(server => server.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(server => server.DomainEvents);
            builder.HasIndex(server => new { server.TeamId, server.Name }).IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(server => server.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServerStatusSnapshot>(builder =>
        {
            builder.ToTable("server_status_snapshots");
            builder.HasKey(snapshot => snapshot.Id);
            builder.Property(snapshot => snapshot.Id).HasServerStatusSnapshotIdConversion();
            builder.Property(snapshot => snapshot.ServerId).HasServerIdConversion();
            builder.Property(snapshot => snapshot.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(snapshot => snapshot.CpuLoadPercent).HasPrecision(7, 2);
            builder.Property(snapshot => snapshot.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(snapshot => snapshot.DomainEvents);
            builder.HasIndex(snapshot => new { snapshot.ServerId, snapshot.CreatedAt });
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(snapshot => snapshot.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureApplications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppEntity>(builder =>
        {
            builder.ToTable("applications");
            builder.HasKey(application => application.Id);
            builder.Property(application => application.Id).HasApplicationIdConversion();
            builder.Property(application => application.EnvironmentId).HasEnvironmentIdConversion();
            builder.Property(application => application.ServerId).HasServerIdConversion();
            builder.Property(application => application.Name).HasConversion(ValueObjectConversions.ResourceName)
                .HasMaxLength(120).IsRequired();
            builder.Property(application => application.Description)
                .HasConversion(ValueObjectConversions.NullableDescription).HasMaxLength(1000);
            builder.Property(application => application.GitSource).HasConversion(ValueObjectConversions.GitSource)
                .HasMaxLength(2600).IsRequired();
            builder.Property(application => application.BuildConfiguration)
                .HasConversion(ValueObjectConversions.BuildConfiguration).HasMaxLength(8192).IsRequired();
            builder.Property(application => application.RuntimeConfiguration)
                .HasConversion(ValueObjectConversions.RuntimeConfiguration).HasMaxLength(1024).IsRequired();
            builder.Property(application => application.DeploymentSettings)
                .HasConversion(ValueObjectConversions.DeploymentSettings).HasMaxLength(1024).IsRequired();
            builder.Property(application => application.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(application => application.DomainEvents);
            builder.HasMany(application => application.Domains)
                .WithOne()
                .HasForeignKey(domain => domain.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Navigation(application => application.Domains).UsePropertyAccessMode(PropertyAccessMode.Field);
            builder.HasIndex(application => new { application.EnvironmentId, application.Name }).IsUnique();
            builder.HasOne<EnvironmentEntity>()
                .WithMany()
                .HasForeignKey(application => application.EnvironmentId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(application => application.ServerId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureProxy(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServerProxySettings>(builder =>
        {
            builder.ToTable("server_proxy_settings");
            builder.HasKey(settings => settings.Id);
            builder.Property(settings => settings.Id).HasColumnName("ServerId").HasServerIdConversion()
                .ValueGeneratedNever();
            builder.Ignore(settings => settings.ServerId);
            builder.Ignore(settings => settings.RoutingEnabled);
            builder.Property(settings => settings.ProxyType).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(settings => settings.Image).HasMaxLength(Vessel.Domain.Proxy.ServerProxySettings.MaxImageLength)
                .IsRequired();
            builder.Property(settings => settings.HttpPort).IsRequired();
            builder.Property(settings => settings.HttpsPort).IsRequired();
            builder.Property(settings => settings.DesiredState).HasConversion<string>().HasMaxLength(32)
                .IsRequired();
            builder.Property(settings => settings.LastAppliedStaticHash)
                .HasMaxLength(Vessel.Domain.Proxy.ServerProxySettings.MaxHashLength);
            builder.Property(settings => settings.LastAppliedDynamicHash)
                .HasMaxLength(Vessel.Domain.Proxy.ServerProxySettings.MaxHashLength);
            builder.Property(settings => settings.LastError).HasMaxLength(Vessel.Domain.Proxy.ServerProxySettings.MaxErrorLength);
            builder.Property(settings => settings.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(settings => settings.DomainEvents);
            builder.HasOne<Server>()
                .WithOne()
                .HasForeignKey<ServerProxySettings>(settings => settings.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureDatabases(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DatabaseResource>(builder =>
        {
            builder.ToTable("database_resources");
            builder.HasKey(database => database.Id);
            builder.Property(database => database.Id).HasDatabaseResourceIdConversion();
            builder.Property(database => database.EnvironmentId).HasEnvironmentIdConversion();
            builder.Property(database => database.ServerId).HasServerIdConversion();
            builder.Property(database => database.Name).HasConversion(ValueObjectConversions.ResourceName)
                .HasMaxLength(120).IsRequired();
            builder.Property(database => database.Description).HasConversion(ValueObjectConversions.NullableDescription)
                .HasMaxLength(1000);
            builder.Property(database => database.Engine).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(database => database.Version).HasConversion(ValueObjectConversions.VersionLabel)
                .HasMaxLength(80).IsRequired();
            builder.Property(database => database.Storage).HasConversion(ValueObjectConversions.StorageConfiguration)
                .HasMaxLength(512).IsRequired();
            builder.Property(database => database.CredentialsReferenceId).HasSecretReferenceIdConversion();
            builder.Property(database => database.HealthState).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(database => database.LifecycleState).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(database => database.ContainerName).HasMaxLength(160);
            builder.Property(database => database.ComposeSnapshotReference).HasMaxLength(512);
            // Null for databases created before images were pinned; they keep their legacy {repo}:{Version} image.
            builder.Property(database => database.Image).HasConversion(ValueObjectConversions.NullableDatabaseImage)
                .HasMaxLength(DatabaseImage.MaxLength);
            builder.Property(database => database.HealthCheckEnabled).HasDefaultValue(true).IsRequired();
            builder.Property(database => database.HealthCheckIntervalSeconds)
                .HasDefaultValue(DatabaseHealthCheckPolicy.DefaultIntervalSeconds).IsRequired();
            builder.Property(database => database.HealthCheckTimeoutSeconds)
                .HasDefaultValue(DatabaseHealthCheckPolicy.DefaultTimeoutSeconds).IsRequired();
            builder.Property(database => database.HealthCheckRetries)
                .HasDefaultValue(DatabaseHealthCheckPolicy.DefaultRetries).IsRequired();
            builder.Property(database => database.HealthCheckStartPeriodSeconds)
                .HasDefaultValue(DatabaseHealthCheckPolicy.DefaultStartPeriodSeconds).IsRequired();
            builder.Property(database => database.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(database => database.DomainEvents);
            builder.Ignore(database => database.HealthCheck);
            builder.HasOne<EnvironmentEntity>()
                .WithMany()
                .HasForeignKey(database => database.EnvironmentId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(database => database.ServerId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(database => database.CredentialsReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureManagedServices(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceResource>(builder =>
        {
            builder.ToTable("service_resources");
            builder.HasKey(service => service.Id);
            builder.Property(service => service.Id).HasServiceResourceIdConversion();
            builder.Property(service => service.TeamId).HasTeamIdConversion();
            builder.Property(service => service.EnvironmentId).HasEnvironmentIdConversion();
            builder.Property(service => service.ServerId).HasServerIdConversion();
            builder.Property(service => service.Name).HasConversion(ValueObjectConversions.ResourceName)
                .HasMaxLength(120).IsRequired();
            builder.Property(service => service.TemplateKey).HasMaxLength(120).IsRequired();
            builder.Property(service => service.TemplateVersion).HasMaxLength(80).IsRequired();
            builder.Property(service => service.ConfigurationJson).HasColumnType("jsonb").IsRequired();
            builder.Property(service => service.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(service => service.ComposeSnapshotReference).HasMaxLength(512);
            builder.Property(service => service.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(service => service.DomainEvents);
            builder.HasIndex(service => new { service.TeamId, service.Name });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(service => service.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<EnvironmentEntity>()
                .WithMany()
                .HasForeignKey(service => service.EnvironmentId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(service => service.ServerId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureBackups(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackupSchedule>(builder =>
        {
            builder.ToTable("backup_schedules");
            builder.HasKey(schedule => schedule.Id);
            builder.Property(schedule => schedule.Id).HasBackupScheduleIdConversion();
            builder.Property(schedule => schedule.TeamId).HasTeamIdConversion();
            builder.Property(schedule => schedule.DatabaseResourceId).HasDatabaseResourceIdConversion();
            builder.Property(schedule => schedule.Name).HasConversion(ValueObjectConversions.ResourceName)
                .HasMaxLength(120).IsRequired();
            builder.Property(schedule => schedule.CronExpression).HasMaxLength(120).IsRequired();
            builder.Property(schedule => schedule.StorageKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(schedule => schedule.TimeoutSeconds).HasDefaultValue(BackupSchedule.DefaultTimeoutSeconds)
                .IsRequired();
            // Existing schedules move to "all databases" so application databases are no longer skipped.
            builder.Property(schedule => schedule.DatabaseSelection)
                .HasMaxLength(BackupDatabaseSelection.MaxStoredLength).HasDefaultValue("*").IsRequired();
            builder.Property(schedule => schedule.DumpFormat).HasConversion<string>().HasMaxLength(32)
                .HasDefaultValue(BackupDumpFormat.Native).IsRequired();
            builder.Property(schedule => schedule.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(schedule => schedule.DomainEvents);
            builder.Ignore(schedule => schedule.Databases);
            builder.Ignore(schedule => schedule.LocalRetention);
            builder.Ignore(schedule => schedule.RemoteRetention);
            builder.Ignore(schedule => schedule.DestinationIds);
            builder.HasMany(schedule => schedule.Destinations)
                .WithOne()
                .HasForeignKey(destination => destination.BackupScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Navigation(schedule => schedule.Destinations).UsePropertyAccessMode(PropertyAccessMode.Field)
                .AutoInclude();
            builder.HasIndex(schedule => new { schedule.Enabled, schedule.MissingAlertDays });
            builder.HasIndex(schedule => new { schedule.TeamId, schedule.DatabaseResourceId, schedule.Name })
                .IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(schedule => schedule.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<DatabaseResource>()
                .WithMany()
                .HasForeignKey(schedule => schedule.DatabaseResourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupExecution>(builder =>
        {
            builder.ToTable("backup_executions");
            builder.HasKey(execution => execution.Id);
            builder.Property(execution => execution.Id).HasBackupExecutionIdConversion();
            builder.Property(execution => execution.TeamId).HasTeamIdConversion();
            builder.Property(execution => execution.DatabaseResourceId).HasDatabaseResourceIdConversion();
            builder.Property(execution => execution.ScheduleId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new BackupScheduleId(value.Value) : null);
            builder.Property(execution => execution.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(execution => execution.StorageKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(execution => execution.ArtifactBucket).HasMaxLength(160);
            builder.Property(execution => execution.ArtifactKey).HasMaxLength(512);
            builder.Property(execution => execution.ChecksumSha256).HasMaxLength(128);
            builder.Property(execution => execution.FailureReason).HasMaxLength(1000);
            builder.Property(execution => execution.LastRestoreFailureReason).HasMaxLength(1000);
            builder.Property(execution => execution.DatabaseName).HasMaxLength(BackupExecution.MaxDatabaseNameLength);
            builder.Property(execution => execution.ArtifactFormat).HasMaxLength(64);
            builder.Property(execution => execution.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(execution => execution.DomainEvents);
            builder.Ignore(execution => execution.HasRestorableArtifact);
            builder.Ignore(execution => execution.HasLocalCopy);
            builder.OwnsMany(execution => execution.Replicas, replica =>
            {
                replica.ToTable("backup_artifact_replicas");
                replica.WithOwner().HasForeignKey(item => item.BackupExecutionId);
                replica.HasKey(item => item.Id);
                replica.Property(item => item.Id).ValueGeneratedNever();
                replica.Property(item => item.BackupExecutionId).HasBackupExecutionIdConversion();
                replica.Property(item => item.BackupDestinationId)
                    .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                        value => value.HasValue ? new BackupDestinationId(value.Value) : null);
                replica.Property(item => item.Bucket).HasMaxLength(63).IsRequired();
                replica.Property(item => item.Message).HasMaxLength(BackupArtifactReplica.MaxMessageLength);
                replica.Ignore(item => item.IsAvailable);
                replica.HasIndex(item => item.BackupDestinationId);
                replica.HasOne<BackupDestination>()
                    .WithMany()
                    .HasForeignKey(item => item.BackupDestinationId)
                    .OnDelete(DeleteBehavior.SetNull);
            });
            builder.Navigation(execution => execution.Replicas).UsePropertyAccessMode(PropertyAccessMode.Field);
            builder.HasIndex(execution => new { execution.TeamId, execution.DatabaseResourceId, execution.CreatedAt });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(execution => execution.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<DatabaseResource>()
                .WithMany()
                .HasForeignKey(execution => execution.DatabaseResourceId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting a schedule deletes its executions (upstream main ce06a7981).
            builder.HasOne<BackupSchedule>()
                .WithMany()
                .HasForeignKey(execution => execution.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupScheduleDestination>(builder =>
        {
            builder.ToTable("backup_schedule_destinations");
            builder.HasKey(destination => new { destination.BackupScheduleId, destination.BackupDestinationId });
            builder.Property(destination => destination.BackupScheduleId).HasBackupScheduleIdConversion();
            builder.Property(destination => destination.BackupDestinationId).HasBackupDestinationIdConversion();
            builder.HasIndex(destination => destination.BackupDestinationId);
            builder.HasOne<BackupDestination>()
                .WithMany()
                .HasForeignKey(destination => destination.BackupDestinationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BackupDestination>(builder =>
        {
            builder.ToTable("backup_destinations");
            builder.HasKey(destination => destination.Id);
            builder.Property(destination => destination.Id).HasBackupDestinationIdConversion();
            builder.Property(destination => destination.TeamId).HasTeamIdConversion();
            builder.Property(destination => destination.Name).HasConversion(ValueObjectConversions.ResourceName)
                .HasMaxLength(120).IsRequired();
            builder.Property(destination => destination.Description)
                .HasConversion(ValueObjectConversions.NullableDescription).HasMaxLength(1000);
            builder.Property(destination => destination.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(destination => destination.Endpoint).HasMaxLength(500).IsRequired();
            builder.Property(destination => destination.Region).HasMaxLength(64).IsRequired();
            builder.Property(destination => destination.Bucket).HasMaxLength(63).IsRequired();
            builder.Property(destination => destination.CredentialsReferenceId).HasSecretReferenceIdConversion();
            builder.Property(destination => destination.LastValidationError)
                .HasMaxLength(BackupDestination.MaxValidationErrorLength);
            builder.Property(destination => destination.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(destination => destination.DomainEvents);
            builder.HasIndex(destination => new { destination.TeamId, destination.Name }).IsUnique();
            builder.HasIndex(destination => destination.IsUsable);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(destination => destination.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(destination => destination.CredentialsReferenceId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<DatabaseRestore>(builder =>
        {
            builder.ToTable("database_restores");
            builder.HasKey(restore => restore.Id);
            builder.Property(restore => restore.Id).HasDatabaseRestoreIdConversion();
            builder.Property(restore => restore.TeamId).HasTeamIdConversion();
            builder.Property(restore => restore.BackupExecutionId).HasBackupExecutionIdConversion();
            builder.Property(restore => restore.TargetDatabaseResourceId).HasDatabaseResourceIdConversion();
            builder.Property(restore => restore.RequestedByUserId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(restore => restore.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(restore => restore.DetectedFormat).HasMaxLength(64);
            builder.Property(restore => restore.Log).HasMaxLength(DatabaseRestore.MaxLogLength).IsRequired();
            builder.Property(restore => restore.FailureReason).HasMaxLength(DatabaseRestore.MaxFailureReasonLength);
            builder.Property(restore => restore.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(restore => restore.DomainEvents);
            builder.Ignore(restore => restore.IsActive);
            builder.HasIndex(restore => new { restore.TeamId, restore.TargetDatabaseResourceId, restore.CreatedAt });
            builder.HasIndex(restore => new { restore.TargetDatabaseResourceId, restore.Status });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(restore => restore.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<BackupExecution>()
                .WithMany()
                .HasForeignKey(restore => restore.BackupExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<DatabaseResource>()
                .WithMany()
                .HasForeignKey(restore => restore.TargetDatabaseResourceId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(restore => restore.RequestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureDeployments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Deployment>(builder =>
        {
            builder.ToTable("deployments");
            builder.HasKey(deployment => deployment.Id);
            builder.Property(deployment => deployment.Id).HasDeploymentIdConversion();
            builder.Property(deployment => deployment.ApplicationId).HasApplicationIdConversion();
            builder.Property(deployment => deployment.ServerId).HasServerIdConversion();
            builder.Property(deployment => deployment.ActorUserId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(deployment => deployment.RollbackDeploymentId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new DeploymentId(value.Value) : null);
            builder.Property(deployment => deployment.CommitSha).HasMaxLength(80);
            builder.Property(deployment => deployment.CommitBranch).HasMaxLength(255);
            builder.Property(deployment => deployment.CommitMessage).HasMaxLength(512);
            builder.Property(deployment => deployment.RepositoryUrl).HasMaxLength(2048);
            builder.Property(deployment => deployment.PreviewId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ApplicationPreviewId(value.Value) : null);
            builder.Property(deployment => deployment.WebhookEventId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new WebhookEventId(value.Value) : null);
            builder.Property(deployment => deployment.ArtifactReference).HasMaxLength(512);
            builder.Property(deployment => deployment.ConfigurationSnapshotReference).HasMaxLength(512);
            builder.Property(deployment => deployment.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(deployment => deployment.Trigger).HasConversion<string>().HasMaxLength(32)
                .HasDefaultValue(DeploymentTrigger.Manual).IsRequired();
            builder.Property(deployment => deployment.ParentDeploymentId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new DeploymentId(value.Value) : null);
            builder.Property(deployment => deployment.RollbackImage).HasMaxLength(512);
            builder.Property(deployment => deployment.CancellationReason).HasMaxLength(512);
            builder.Property(deployment => deployment.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(deployment => deployment.DomainEvents);
            builder.HasMany(deployment => deployment.LogLines)
                .WithOne()
                .HasForeignKey(line => line.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Navigation(deployment => deployment.LogLines).UsePropertyAccessMode(PropertyAccessMode.Field);
            builder.HasIndex(deployment => new { deployment.ApplicationId, deployment.CreatedAt });
            // Queue admission and advancement scan active deployments per server.
            builder.HasIndex(deployment => new { deployment.ServerId, deployment.Status, deployment.CreatedAt });
            builder.HasOne<AppEntity>()
                .WithMany()
                .HasForeignKey(deployment => deployment.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(deployment => deployment.ServerId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(deployment => deployment.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne<ApplicationPreview>()
                .WithMany()
                .HasForeignKey(deployment => deployment.PreviewId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne<WebhookEvent>()
                .WithMany()
                .HasForeignKey(deployment => deployment.WebhookEventId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DeploymentLogLine>(builder =>
        {
            builder.ToTable("deployment_log_lines");
            builder.HasKey(line => new { line.DeploymentId, line.Sequence });
            builder.Property(line => line.DeploymentId).HasDeploymentIdConversion();
            builder.Property(line => line.Stream).HasMaxLength(16).IsRequired();
            builder.Property(line => line.Message).IsRequired();
        });
    }

    private static void ConfigureSecrets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SecretReference>(builder =>
        {
            builder.ToTable("secret_references");
            builder.HasKey(secret => secret.Id);
            builder.Property(secret => secret.Id).HasSecretReferenceIdConversion();
            builder.Property(secret => secret.TeamId).HasTeamIdConversion();
            builder.Property(secret => secret.ProjectId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ProjectId(value.Value) : null);
            builder.Property(secret => secret.EnvironmentId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new EnvironmentId(value.Value) : null);
            builder.Property(secret => secret.ServerId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ServerId(value.Value) : null);
            builder.Property(secret => secret.ApplicationId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new AppId(value.Value) : null);
            builder.Property(secret => secret.DatabaseResourceId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new DatabaseResourceId(value.Value) : null);
            builder.Property(secret => secret.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(secret => secret.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(secret => secret.Key).HasMaxLength(160).IsRequired();
            builder.Property(secret => secret.ProviderReference).HasMaxLength(512).IsRequired();
            builder.Property(secret => secret.Policy).HasConversion(ValueObjectConversions.SecretPolicy)
                .HasMaxLength(64).IsRequired();
            builder.Property(secret => secret.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(secret => secret.DomainEvents);
            builder.HasIndex(secret => new { secret.TeamId, secret.Scope, secret.Key }).IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(secret => secret.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SecretValue>(builder =>
        {
            builder.ToTable("secret_values");
            builder.HasKey(secret => secret.Id);
            builder.Property(secret => secret.Id).HasSecretValueIdConversion();
            builder.Property(secret => secret.SecretReferenceId).HasSecretReferenceIdConversion();
            builder.Property(secret => secret.CipherText).HasMaxLength(12000).IsRequired();
            builder.Property(secret => secret.Nonce).HasMaxLength(128).IsRequired();
            builder.Property(secret => secret.Tag).HasMaxLength(128).IsRequired();
            builder.Property(secret => secret.KeyVersion).HasMaxLength(80).IsRequired();
            builder.Property(secret => secret.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(secret => secret.DomainEvents);
            builder.HasIndex(secret => secret.SecretReferenceId).IsUnique();
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(secret => secret.SecretReferenceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureTerminals(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TerminalSession>(builder =>
        {
            builder.ToTable("terminal_sessions");
            builder.HasKey(session => session.Id);
            builder.Property(session => session.Id).HasTerminalSessionIdConversion();
            builder.Property(session => session.TeamId).HasTeamIdConversion();
            builder.Property(session => session.OwnerUserId).HasUserIdConversion();
            builder.Property(session => session.ServerId).HasServerIdConversion();
            builder.Property(session => session.TargetType).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(session => session.ContainerName).HasMaxLength(255);
            builder.Property(session => session.Command).HasMaxLength(240).IsRequired();
            builder.Property(session => session.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(session => session.FailureReason).HasMaxLength(512);
            builder.Property(session => session.Mode).HasConversion<string>().HasMaxLength(16)
                .HasDefaultValue(TerminalMode.Shell).HasSentinel((TerminalMode)(-1)).IsRequired();
            builder.Property(session => session.ResourceKind).HasConversion<string>().HasMaxLength(32);
            builder.Property(session => session.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(session => session.DomainEvents);
            builder.HasIndex(session => new { session.TeamId, session.Status, session.StartedAt });
            builder.HasIndex(session => new { session.OwnerUserId, session.StartedAt });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(session => session.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(session => session.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(session => session.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureEnvironmentVariables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnvironmentVariable>(builder =>
        {
            builder.ToTable("environment_variables");
            builder.HasKey(variable => variable.Id);
            builder.Property(variable => variable.Id).HasEnvironmentVariableIdConversion();
            builder.Property(variable => variable.TeamId).HasTeamIdConversion();
            builder.Property(variable => variable.ProjectId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ProjectId(value.Value) : null);
            builder.Property(variable => variable.EnvironmentId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new EnvironmentId(value.Value) : null);
            builder.Property(variable => variable.ServerId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ServerId(value.Value) : null);
            builder.Property(variable => variable.ApplicationId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new AppId(value.Value) : null);
            builder.Property(variable => variable.DatabaseResourceId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new DatabaseResourceId(value.Value) : null);
            builder.Property(variable => variable.TargetType).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(variable => variable.Key).HasConversion(ValueObjectConversions.EnvironmentVariableKey)
                .HasMaxLength(160).IsRequired();
            builder.Property(variable => variable.ValueKind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(variable => variable.PlainValue).HasMaxLength(8000);
            builder.Property(variable => variable.SecretReferenceId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new SecretReferenceId(value.Value) : null);
            builder.Property(variable => variable.Comment).HasMaxLength(1000);
            // DEP-10: new variables use exact values; the migration sets existing rows to true (legacy escaping).
            builder.Property(variable => variable.UsesLegacyEscaping).HasDefaultValue(false);
            builder.Property(variable => variable.PreserveComposeNull).HasDefaultValue(false);
            builder.Property(variable => variable.SortOrder).HasDefaultValue(0);
            builder.Property(variable => variable.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(variable => variable.DomainEvents);
            builder.HasIndex(variable => new
            {
                variable.TeamId,
                variable.TargetType,
                variable.ProjectId,
                variable.EnvironmentId,
                variable.ServerId,
                variable.ApplicationId,
                variable.DatabaseResourceId,
                variable.Key,
                variable.IsPreview
            }).IsUnique().AreNullsDistinct(false);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(variable => variable.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(variable => variable.SecretReferenceId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureSshKeys(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SshKey>(builder =>
        {
            builder.ToTable("ssh_keys");
            builder.HasKey(key => key.Id);
            builder.Property(key => key.Id).HasSshKeyIdConversion();
            builder.Property(key => key.TeamId).HasTeamIdConversion();
            builder.Property(key => key.Name).HasConversion(ValueObjectConversions.ResourceName).HasMaxLength(120)
                .IsRequired();
            builder.Property(key => key.Description).HasConversion(ValueObjectConversions.NullableDescription)
                .HasMaxLength(1000);
            builder.Property(key => key.PrivateKeyReferenceId).HasSecretReferenceIdConversion();
            builder.Property(key => key.PublicKey).HasMaxLength(SshKey.MaxPublicKeyLength).IsRequired();
            builder.Property(key => key.Fingerprint).HasMaxLength(SshKey.MaxFingerprintLength).IsRequired();
            builder.Property(key => key.Algorithm).HasMaxLength(SshKey.MaxAlgorithmLength).IsRequired();
            builder.Property(key => key.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(key => key.DomainEvents);
            builder.HasIndex(key => new { key.TeamId, key.Name }).IsUnique();
            builder.HasIndex(key => new { key.TeamId, key.Fingerprint }).IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(key => key.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(key => key.PrivateKeyReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureRegistryCredentials(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RegistryCredential>(builder =>
        {
            builder.ToTable("registry_credentials");
            builder.HasKey(credential => credential.Id);
            builder.Property(credential => credential.Id).HasRegistryCredentialIdConversion();
            builder.Property(credential => credential.TeamId).HasTeamIdConversion();
            builder.Property(credential => credential.Name).HasConversion(ValueObjectConversions.ResourceName)
                .HasMaxLength(120).IsRequired();
            builder.Property(credential => credential.Registry).HasMaxLength(255).IsRequired();
            builder.Property(credential => credential.Username).HasMaxLength(255).IsRequired();
            builder.Property(credential => credential.PasswordReferenceId).HasSecretReferenceIdConversion();
            builder.Property(credential => credential.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(credential => credential.DomainEvents);
            builder.HasIndex(credential => new { credential.TeamId, credential.Registry, credential.Username })
                .IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(credential => credential.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(credential => credential.PasswordReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureNotifications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationTarget>(builder =>
        {
            builder.ToTable("notification_targets");
            builder.HasKey(target => target.Id);
            builder.Property(target => target.Id).HasNotificationTargetIdConversion();
            builder.Property(target => target.TeamId).HasTeamIdConversion();
            builder.Property(target => target.Name).HasConversion(ValueObjectConversions.ResourceName).HasMaxLength(120)
                .IsRequired();
            builder.Property(target => target.Channel).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(target => target.CredentialsReferenceId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new SecretReferenceId(value.Value) : null);
            builder.Property(target => target.ConfigurationJson).HasColumnType("jsonb").IsRequired();
            builder.Property(target => target.Policy).HasConversion(ValueObjectConversions.NotificationDeliveryPolicy)
                .HasMaxLength(64).IsRequired();
            builder.Property(target => target.IsEnabled).IsRequired();
            builder.Property(target => target.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(target => target.DomainEvents);
            builder.HasIndex(target => new { target.TeamId, target.Name }).IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(target => target.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(target => target.CredentialsReferenceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<NotificationEvent>(builder =>
        {
            builder.ToTable("notification_events");
            builder.HasKey(notification => notification.Id);
            builder.Property(notification => notification.Id).HasNotificationEventIdConversion();
            builder.Property(notification => notification.TeamId).HasTeamIdConversion();
            builder.Property(notification => notification.UserId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(notification => notification.EventType).HasMaxLength(120).IsRequired();
            builder.Property(notification => notification.Severity).HasConversion<string>().HasMaxLength(32)
                .IsRequired();
            builder.Property(notification => notification.TargetType).HasConversion<string>().HasMaxLength(32)
                .IsRequired();
            builder.Property(notification => notification.TargetId).HasMaxLength(160);
            builder.Property(notification => notification.Title).HasMaxLength(255).IsRequired();
            builder.Property(notification => notification.Message).HasMaxLength(2000).IsRequired();
            builder.Property(notification => notification.PayloadJson).HasColumnType("jsonb").IsRequired();
            builder.Property(notification => notification.ResourceUrl).HasMaxLength(2048);
            builder.Property(notification => notification.Status).HasConversion<string>().HasMaxLength(32)
                .IsRequired();
            builder.Property(notification => notification.FailureReason).HasMaxLength(512);
            builder.Property(notification => notification.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(notification => notification.DomainEvents);
            builder.HasIndex(notification => new { notification.TeamId, notification.CreatedAt });
            builder.HasIndex(notification => new { notification.TeamId, notification.Status, notification.CreatedAt });
            builder.HasIndex(notification => new { notification.EventType, notification.CreatedAt });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(notification => notification.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(notification => notification.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InAppNotification>(builder =>
        {
            builder.ToTable("in_app_notifications");
            builder.HasKey(notification => notification.Id);
            builder.Property(notification => notification.Id).HasInAppNotificationIdConversion();
            builder.Property(notification => notification.EventId).HasNotificationEventIdConversion();
            builder.Property(notification => notification.TeamId).HasTeamIdConversion();
            builder.Property(notification => notification.UserId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(notification => notification.Status).HasConversion<string>().HasMaxLength(32)
                .IsRequired();
            builder.Property(notification => notification.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(notification => notification.DomainEvents);
            builder.HasIndex(notification => new { notification.TeamId, notification.Status, notification.CreatedAt });
            builder.HasIndex(notification => new { notification.UserId, notification.Status, notification.CreatedAt });
            builder.HasOne<NotificationEvent>()
                .WithMany()
                .HasForeignKey(notification => notification.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(notification => notification.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(notification => notification.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<NotificationDeliveryAttempt>(builder =>
        {
            builder.ToTable("notification_delivery_attempts");
            builder.HasKey(attempt => attempt.Id);
            builder.Property(attempt => attempt.Id).HasNotificationDeliveryAttemptIdConversion();
            builder.Property(attempt => attempt.EventId).HasNotificationEventIdConversion();
            builder.Property(attempt => attempt.TargetId).HasNotificationTargetIdConversion();
            builder.Property(attempt => attempt.Channel).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(attempt => attempt.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(attempt => attempt.FailureReason).HasMaxLength(512);
            builder.Property(attempt => attempt.ProviderMessageId).HasMaxLength(255);
            builder.Property(attempt => attempt.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(attempt => attempt.DomainEvents);
            builder.HasIndex(attempt => new { attempt.EventId, attempt.TargetId, attempt.AttemptNumber }).IsUnique();
            builder.HasIndex(attempt => new { attempt.Status, attempt.RetryAfter });
            builder.HasOne<NotificationEvent>()
                .WithMany()
                .HasForeignKey(attempt => attempt.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<NotificationTarget>()
                .WithMany()
                .HasForeignKey(attempt => attempt.TargetId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAuditLogs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLog>(builder =>
        {
            builder.ToTable("audit_logs");
            builder.HasKey(audit => audit.Id);
            builder.Property(audit => audit.Id).HasAuditLogIdConversion();
            builder.Property(audit => audit.TeamId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new TeamId(value.Value) : null);
            builder.Property(audit => audit.ActorUserId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new UserId(value.Value) : null);
            builder.Property(audit => audit.Action).HasMaxLength(160).IsRequired();
            builder.Property(audit => audit.Target).HasConversion(ValueObjectConversions.AuditTarget).HasMaxLength(320)
                .IsRequired();
            builder.Property(audit => audit.CorrelationId).HasMaxLength(120);
            builder.Property(audit => audit.RedactedMetadataJson).HasColumnType("jsonb").IsRequired();
            builder.Ignore(audit => audit.DomainEvents);
            builder.HasIndex(audit => new { audit.TeamId, audit.CreatedAt });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(audit => audit.TeamId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(audit => audit.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SettingEntry>(builder =>
        {
            builder.ToTable("settings");
            builder.HasKey(setting => setting.Id);
            builder.Property(setting => setting.Id).HasSettingIdConversion();
            builder.Property(setting => setting.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(setting => setting.TeamId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new TeamId(value.Value) : null);
            builder.Property(setting => setting.ProjectId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ProjectId(value.Value) : null);
            builder.Property(setting => setting.ResourceType).HasMaxLength(120);
            builder.Property(setting => setting.ResourceId).HasMaxLength(160);
            builder.Property(setting => setting.Key).HasMaxLength(160).IsRequired();
            builder.Property(setting => setting.Value).HasMaxLength(4000).IsRequired();
            builder.Property(setting => setting.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(setting => setting.DomainEvents);
            builder.HasIndex(setting => new
            {
                setting.Scope,
                setting.TeamId,
                setting.ProjectId,
                setting.ResourceType,
                setting.ResourceId,
                setting.Key
            }).IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(setting => setting.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Project>()
                .WithMany()
                .HasForeignKey(setting => setting.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureWebhooks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookEvent>(builder =>
        {
            builder.ToTable("webhook_events");
            builder.HasKey(webhook => webhook.Id);
            builder.Property(webhook => webhook.Id).HasWebhookEventIdConversion();
            builder.Property(webhook => webhook.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(webhook => webhook.EventType).HasMaxLength(120).IsRequired();
            builder.Property(webhook => webhook.ProviderEventId).HasMaxLength(255);
            builder.Property(webhook => webhook.DedupeKey).HasMaxLength(512).IsRequired();
            builder.Property(webhook => webhook.PayloadReference).HasMaxLength(512).IsRequired();
            builder.Property(webhook => webhook.PayloadJson).IsRequired();
            builder.Property(webhook => webhook.SignatureStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(webhook => webhook.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(webhook => webhook.FailureReason).HasMaxLength(512);
            // Applications whose own secret verified the delivery (SEC-01); stored as a PostgreSQL uuid[] column.
            builder.PrimitiveCollection(webhook => webhook.VerifiedApplicationIds).IsRequired();
            // Per-application fan-out results (DEP-12), stored as jsonb.
            builder.Property(webhook => webhook.ApplicationOutcomes)
                .HasConversion(WebhookOutcomeConversions.Converter, WebhookOutcomeConversions.Comparer)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .IsRequired();
            builder.Property(webhook => webhook.ApplicationId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new AppId(value.Value) : null);
            builder.Property(webhook => webhook.DeploymentId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new DeploymentId(value.Value) : null);
            builder.Property(webhook => webhook.PreviewId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new ApplicationPreviewId(value.Value) : null);
            builder.Property(webhook => webhook.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(webhook => webhook.DomainEvents);
            builder.HasIndex(webhook => webhook.DedupeKey).IsUnique();
            builder.HasIndex(webhook => new { webhook.Provider, webhook.EventType, webhook.CreatedAt });
            builder.HasOne<AppEntity>()
                .WithMany()
                .HasForeignKey(webhook => webhook.ApplicationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ApplicationWebhookConfiguration>(builder =>
        {
            builder.ToTable("application_webhook_configurations");
            builder.HasKey(configuration => configuration.Id);
            builder.Property(configuration => configuration.Id).HasApplicationWebhookConfigurationIdConversion();
            builder.Property(configuration => configuration.ApplicationId).HasApplicationIdConversion();
            builder.Property(configuration => configuration.Provider).HasConversion<string>().HasMaxLength(32)
                .IsRequired();
            builder.Property(configuration => configuration.SecretReferenceId).HasSecretReferenceIdConversion();
            builder.Property(configuration => configuration.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(configuration => configuration.DomainEvents);
            builder.HasIndex(configuration => new { configuration.ApplicationId, configuration.Provider }).IsUnique();
            builder.HasOne<AppEntity>()
                .WithMany()
                .HasForeignKey(configuration => configuration.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(configuration => configuration.SecretReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApplicationPreview>(builder =>
        {
            builder.ToTable("application_previews");
            builder.HasKey(preview => preview.Id);
            builder.Property(preview => preview.Id).HasApplicationPreviewIdConversion();
            builder.Property(preview => preview.ApplicationId).HasApplicationIdConversion();
            builder.Property(preview => preview.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(preview => preview.SourceBranch).HasMaxLength(255).IsRequired();
            builder.Property(preview => preview.TargetBranch).HasMaxLength(255).IsRequired();
            builder.Property(preview => preview.CommitSha).HasMaxLength(80).IsRequired();
            builder.Property(preview => preview.PullRequestUrl).HasMaxLength(2048);
            builder.Property(preview => preview.Title).HasMaxLength(255);
            builder.Property(preview => preview.PreviewUrl).HasMaxLength(2048);
            builder.Property(preview => preview.SourceRepository).HasMaxLength(512);
            builder.Property(preview => preview.IsSameRepository).IsRequired();
            builder.Property(preview => preview.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(preview => preview.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(preview => preview.DomainEvents);
            builder.HasIndex(preview => new { preview.ApplicationId, preview.Provider, preview.PullRequestNumber })
                .IsUnique();
            builder.HasOne<AppEntity>()
                .WithMany()
                .HasForeignKey(preview => preview.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
