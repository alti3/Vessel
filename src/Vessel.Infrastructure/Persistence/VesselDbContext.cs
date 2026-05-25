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
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Infrastructure.Persistence;

public sealed class VesselDbContext : DbContext, IVesselDbContext
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
        DeploymentRepository = new EfRepository<Deployment, DeploymentId>(this);
        SecretReferenceRepository = new EfRepository<SecretReference, SecretReferenceId>(this);
        SecretValueRepository = new EfRepository<SecretValue, SecretValueId>(this);
        EnvironmentVariableRepository = new EfRepository<EnvironmentVariable, EnvironmentVariableId>(this);
        RegistryCredentialRepository = new EfRepository<RegistryCredential, RegistryCredentialId>(this);
        ServerStatusSnapshotRepository = new EfRepository<ServerStatusSnapshot, ServerStatusSnapshotId>(this);
        WebhookEventRepository = new EfRepository<WebhookEvent, WebhookEventId>(this);
        ApplicationWebhookConfigurationRepository =
            new EfRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>(this);
        ApplicationPreviewRepository = new EfRepository<ApplicationPreview, ApplicationPreviewId>(this);
        ProxyConfigurationVersionRepository =
            new EfRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId>(this);
        CertificateRepository = new EfRepository<Certificate, CertificateId>(this);
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

    public DbSet<Deployment> DeploymentSet => Set<Deployment>();

    public DbSet<SecretReference> SecretReferenceSet => Set<SecretReference>();

    public DbSet<SecretValue> SecretValueSet => Set<SecretValue>();

    public DbSet<EnvironmentVariable> EnvironmentVariableSet => Set<EnvironmentVariable>();

    public DbSet<RegistryCredential> RegistryCredentialSet => Set<RegistryCredential>();

    public DbSet<ServerStatusSnapshot> ServerStatusSnapshotSet => Set<ServerStatusSnapshot>();

    public DbSet<NotificationTarget> NotificationTargetSet => Set<NotificationTarget>();

    public DbSet<AuditLog> AuditLogSet => Set<AuditLog>();

    public DbSet<SettingEntry> SettingSet => Set<SettingEntry>();

    public DbSet<WebhookEvent> WebhookEventSet => Set<WebhookEvent>();

    public DbSet<ApplicationWebhookConfiguration> ApplicationWebhookConfigurationSet =>
        Set<ApplicationWebhookConfiguration>();

    public DbSet<ApplicationPreview> ApplicationPreviewSet => Set<ApplicationPreview>();

    public DbSet<ProxyConfigurationVersion> ProxyConfigurationVersionSet => Set<ProxyConfigurationVersion>();

    public DbSet<Certificate> CertificateSet => Set<Certificate>();

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

    public IQueryable<Deployment> Deployments => DeploymentSet;

    public IQueryable<SecretReference> SecretReferences => SecretReferenceSet;

    public IQueryable<SecretValue> SecretValues => SecretValueSet;

    public IQueryable<EnvironmentVariable> EnvironmentVariables => EnvironmentVariableSet;

    public IQueryable<RegistryCredential> RegistryCredentials => RegistryCredentialSet;

    public IQueryable<ServerStatusSnapshot> ServerStatusSnapshots => ServerStatusSnapshotSet;

    public IQueryable<NotificationTarget> NotificationTargets => NotificationTargetSet;

    public IQueryable<AuditLog> AuditLogs => AuditLogSet;

    public IQueryable<SettingEntry> Settings => SettingSet;

    public IQueryable<WebhookEvent> WebhookEvents => WebhookEventSet;

    public IQueryable<ApplicationWebhookConfiguration> ApplicationWebhookConfigurations =>
        ApplicationWebhookConfigurationSet;

    public IQueryable<ApplicationPreview> ApplicationPreviews => ApplicationPreviewSet;

    public IQueryable<ProxyConfigurationVersion> ProxyConfigurationVersions => ProxyConfigurationVersionSet;

    public IQueryable<Certificate> Certificates => CertificateSet;

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

    public IRepository<Deployment, DeploymentId> DeploymentRepository { get; }

    public IRepository<SecretReference, SecretReferenceId> SecretReferenceRepository { get; }

    public IRepository<SecretValue, SecretValueId> SecretValueRepository { get; }

    public IRepository<EnvironmentVariable, EnvironmentVariableId> EnvironmentVariableRepository { get; }

    public IRepository<RegistryCredential, RegistryCredentialId> RegistryCredentialRepository { get; }

    public IRepository<ServerStatusSnapshot, ServerStatusSnapshotId> ServerStatusSnapshotRepository { get; }

    public IRepository<WebhookEvent, WebhookEventId> WebhookEventRepository { get; }

    public IRepository<ApplicationWebhookConfiguration, ApplicationWebhookConfigurationId>
        ApplicationWebhookConfigurationRepository { get; }

    public IRepository<ApplicationPreview, ApplicationPreviewId> ApplicationPreviewRepository { get; }

    public IRepository<ProxyConfigurationVersion, ProxyConfigurationVersionId> ProxyConfigurationVersionRepository
    {
        get;
    }

    public IRepository<Certificate, CertificateId> CertificateRepository { get; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("vessel");

        ConfigureUsers(modelBuilder);
        ConfigureTeams(modelBuilder);
        ConfigureProjects(modelBuilder);
        ConfigureServers(modelBuilder);
        ConfigureApplications(modelBuilder);
        ConfigureDatabases(modelBuilder);
        ConfigureManagedServices(modelBuilder);
        ConfigureBackups(modelBuilder);
        ConfigureDeployments(modelBuilder);
        ConfigureSecrets(modelBuilder);
        ConfigureEnvironmentVariables(modelBuilder);
        ConfigureRegistryCredentials(modelBuilder);
        ConfigureNotifications(modelBuilder);
        ConfigureAuditLogs(modelBuilder);
        ConfigureSettings(modelBuilder);
        ConfigureWebhooks(modelBuilder);
        ConfigureProxy(modelBuilder);
        ConfigureCertificates(modelBuilder);
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
            builder.Property(user => user.TwoFactorSecret).HasMaxLength(128);
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
                .HasConversion(ValueObjectConversions.BuildConfiguration).HasMaxLength(2048).IsRequired();
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

        modelBuilder.Entity<ApplicationDomain>(builder =>
        {
            builder.ToTable("application_domains");
            builder.HasKey(domain => new { domain.ApplicationId, domain.DomainName });
            builder.Property(domain => domain.ApplicationId).HasApplicationIdConversion();
            builder.Property(domain => domain.DomainName).HasConversion(ValueObjectConversions.DomainName)
                .HasMaxLength(253).IsRequired();
            builder.Property(domain => domain.TargetPort);
            builder.Property(domain => domain.TlsEnabled).IsRequired();
            builder.Property(domain => domain.Canonical).IsRequired();
            builder.Property(domain => domain.RedirectToCanonical).IsRequired();
        });
    }

    private static void ConfigureProxy(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProxyConfigurationVersion>(builder =>
        {
            builder.ToTable("proxy_configuration_versions");
            builder.HasKey(version => version.Id);
            builder.Property(version => version.Id).HasProxyConfigurationVersionIdConversion();
            builder.Property(version => version.ServerId).HasServerIdConversion();
            builder.Property(version => version.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(version => version.Version).HasMaxLength(80).IsRequired();
            builder.Property(version => version.ConfigurationHash).HasMaxLength(128).IsRequired();
            builder.Property(version => version.Configuration).IsRequired();
            builder.Property(version => version.PreviousVersionId)
                .HasConversion(StronglyTypedIdConversions.ProxyConfigurationVersionId);
            builder.Property(version => version.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(version => version.ValidationError).HasMaxLength(2000);
            builder.Property(version => version.ApplyError).HasMaxLength(2000);
            builder.Property(version => version.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(version => version.DomainEvents);
            builder.HasIndex(version => new { version.ServerId, version.CreatedAt });
            builder.HasIndex(version => new { version.ServerId, version.ConfigurationHash });
            builder.HasIndex(version => version.PreviousVersionId);
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(version => version.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<ProxyConfigurationVersion>()
                .WithMany()
                .HasForeignKey(version => version.PreviousVersionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureCertificates(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Certificate>(builder =>
        {
            builder.ToTable("certificates");
            builder.HasKey(certificate => certificate.Id);
            builder.Property(certificate => certificate.Id).HasCertificateIdConversion();
            builder.Property(certificate => certificate.TeamId).HasTeamIdConversion();
            builder.Property(certificate => certificate.ApplicationId).HasApplicationIdConversion();
            builder.Property(certificate => certificate.Host).HasMaxLength(253).IsRequired();
            builder.Property(certificate => certificate.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(certificate => certificate.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(certificate => certificate.LastError).HasMaxLength(2000);
            builder.Property(certificate => certificate.CertificateSecretReferenceId)
                .HasConversion(StronglyTypedIdConversions.SecretReferenceId);
            builder.Property(certificate => certificate.PrivateKeySecretReferenceId)
                .HasConversion(StronglyTypedIdConversions.SecretReferenceId);
            builder.Property(certificate => certificate.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(certificate => certificate.DomainEvents);
            builder.HasIndex(certificate => new { certificate.ApplicationId, certificate.Host }).IsUnique();
            builder.HasIndex(certificate => new { certificate.TeamId, certificate.RenewalDueAt });
            builder.HasIndex(certificate => certificate.CertificateSecretReferenceId);
            builder.HasIndex(certificate => certificate.PrivateKeySecretReferenceId);
            builder.HasOne<AppEntity>()
                .WithMany()
                .HasForeignKey(certificate => certificate.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(certificate => certificate.CertificateSecretReferenceId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne<SecretReference>()
                .WithMany()
                .HasForeignKey(certificate => certificate.PrivateKeySecretReferenceId)
                .OnDelete(DeleteBehavior.SetNull);
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
            builder.Property(database => database.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(database => database.DomainEvents);
            builder.HasMany(database => database.BackupPolicies)
                .WithOne()
                .HasForeignKey(policy => policy.DatabaseResourceId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Navigation(database => database.BackupPolicies).UsePropertyAccessMode(PropertyAccessMode.Field);
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

        modelBuilder.Entity<BackupPolicy>(builder =>
        {
            builder.ToTable("database_backup_policies");
            builder.HasKey(policy => new { policy.DatabaseResourceId, policy.CronExpression });
            builder.Property(policy => policy.DatabaseResourceId).HasDatabaseResourceIdConversion();
            builder.Property(policy => policy.CronExpression).HasMaxLength(120);
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
            builder.Property(schedule => schedule.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(schedule => schedule.DomainEvents);
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
            builder.Property(execution => execution.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(execution => execution.DomainEvents);
            builder.HasIndex(execution => new { execution.TeamId, execution.DatabaseResourceId, execution.CreatedAt });
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(execution => execution.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<DatabaseResource>()
                .WithMany()
                .HasForeignKey(execution => execution.DatabaseResourceId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<BackupSchedule>()
                .WithMany()
                .HasForeignKey(execution => execution.ScheduleId)
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
            builder.Property(deployment => deployment.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(deployment => deployment.DomainEvents);
            builder.HasMany(deployment => deployment.LogLines)
                .WithOne()
                .HasForeignKey(line => line.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Navigation(deployment => deployment.LogLines).UsePropertyAccessMode(PropertyAccessMode.Field);
            builder.HasIndex(deployment => new { deployment.ApplicationId, deployment.CreatedAt });
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
                variable.Key
            }).IsUnique();
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
            builder.Property(target => target.Policy).HasConversion(ValueObjectConversions.NotificationDeliveryPolicy)
                .HasMaxLength(64).IsRequired();
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
