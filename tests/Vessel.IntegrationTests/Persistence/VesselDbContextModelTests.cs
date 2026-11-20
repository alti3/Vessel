using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Vessel.Domain.Backups;
using Vessel.Domain.Certificates;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Proxy;
using Vessel.Domain.Registries;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using Vessel.Domain.Teams;
using Vessel.Domain.Terminals;
using Vessel.Domain.Webhooks;
using Vessel.Infrastructure.Persistence;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.IntegrationTests.Persistence;

public sealed class VesselDbContextModelTests
{
    [Fact]
    public void Model_MapsCoreTablesToVesselSchema()
    {
        using VesselDbContext context = CreateContext();

        Assert.Equal("vessel", context.Model.FindEntityType(typeof(Team))?.GetSchema());
        Assert.Equal("teams", context.Model.FindEntityType(typeof(Team))?.GetTableName());
        Assert.Equal("applications", context.Model.FindEntityType(typeof(AppEntity))?.GetTableName());
        Assert.Equal("environment_variables",
            context.Model.FindEntityType(typeof(EnvironmentVariable))?.GetTableName());
        Assert.Equal("secret_values", context.Model.FindEntityType(typeof(SecretValue))?.GetTableName());
        Assert.Equal("registry_credentials", context.Model.FindEntityType(typeof(RegistryCredential))?.GetTableName());
        Assert.Equal("server_status_snapshots",
            context.Model.FindEntityType(typeof(ServerStatusSnapshot))?.GetTableName());
    }

    [Fact]
    public void Model_KeepsDescriptionsNullable()
    {
        using VesselDbContext context = CreateContext();

        var isNullable = context.Model
            .FindEntityType(typeof(Team))
            ?.FindProperty(nameof(Team.Description))
            ?.IsNullable;

        Assert.True(isNullable);
    }

    [Fact]
    public void Model_ContainsInitialMigration()
    {
        using VesselDbContext context = CreateContext();

        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("InitialDomainModel", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("Phase4Auth", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("Phase8DeploymentMvp", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("Phase9WebhooksAndPreviews", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("Phase10ProxyDomainsTls", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("Phase11ManagedServicesBackups", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("Phase12TerminalLogsMonitoring", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateScript_ContainsCoreForeignKeysAndIndexes()
    {
        using VesselDbContext context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE SCHEMA", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FK_applications_environments_EnvironmentId", script, StringComparison.Ordinal);
        Assert.Contains("IX_team_memberships_UserId", script, StringComparison.Ordinal);
        Assert.Contains("personal_access_tokens", script, StringComparison.Ordinal);
        Assert.Contains("team_invitations", script, StringComparison.Ordinal);
        Assert.Contains("environment_variables", script, StringComparison.Ordinal);
        Assert.Contains("secret_values", script, StringComparison.Ordinal);
        Assert.Contains("registry_credentials", script, StringComparison.Ordinal);
        Assert.Contains("server_status_snapshots", script, StringComparison.Ordinal);
        Assert.Contains("webhook_events", script, StringComparison.Ordinal);
        Assert.Contains("application_webhook_configurations", script, StringComparison.Ordinal);
        Assert.Contains("application_previews", script, StringComparison.Ordinal);
        Assert.Contains("proxy_configuration_versions", script, StringComparison.Ordinal);
        Assert.Contains("certificates", script, StringComparison.Ordinal);
        Assert.Contains("backup_schedules", script, StringComparison.Ordinal);
        Assert.Contains("backup_executions", script, StringComparison.Ordinal);
        Assert.Contains("service_resources", script, StringComparison.Ordinal);
        Assert.Contains("LifecycleState", script, StringComparison.Ordinal);
        Assert.Contains("ConfigurationSnapshotReference", script, StringComparison.Ordinal);
        Assert.Contains("CancellationRequestedAt", script, StringComparison.Ordinal);
        Assert.Contains("WebhookEventId", script, StringComparison.Ordinal);
        Assert.Contains("PreviewId", script, StringComparison.Ordinal);
        Assert.Contains("terminal_sessions", script, StringComparison.Ordinal);
        Assert.Contains("IX_terminal_sessions_TeamId_Status_StartedAt", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_MapsPhase12TerminalSessions()
    {
        using VesselDbContext context = CreateContext();

        IEntityType? terminalSession = context.Model.FindEntityType(typeof(TerminalSession));

        Assert.Equal("terminal_sessions", terminalSession?.GetTableName());
        Assert.Equal(240, terminalSession?.FindProperty(nameof(TerminalSession.Command))?.GetMaxLength());
        Assert.Equal(512, terminalSession?.FindProperty(nameof(TerminalSession.FailureReason))?.GetMaxLength());
    }

    [Fact]
    public void Model_MapsPhase10ProxyAndCertificateTables()
    {
        using VesselDbContext context = CreateContext();

        IEntityType? proxyVersion = context.Model.FindEntityType(typeof(ProxyConfigurationVersion));
        IEntityType? certificate = context.Model.FindEntityType(typeof(Certificate));

        Assert.Equal("proxy_configuration_versions", proxyVersion?.GetTableName());
        Assert.Equal(128,
            proxyVersion?.FindProperty(nameof(ProxyConfigurationVersion.ConfigurationHash))?.GetMaxLength());
        Assert.Equal("certificates", certificate?.GetTableName());
        Assert.Equal(253, certificate?.FindProperty(nameof(Certificate.Host))?.GetMaxLength());
    }

    [Fact]
    public void Model_MapsServerProxySettingsKeyedByServer()
    {
        using VesselDbContext context = CreateContext();

        IEntityType? settings = context.Model.FindEntityType(typeof(ServerProxySettings));

        Assert.Equal("server_proxy_settings", settings?.GetTableName());
        IProperty key = Assert.Single(settings!.FindPrimaryKey()!.Properties);
        Assert.Equal("ServerId", key.GetColumnName());
        Assert.Equal(255, settings.FindProperty(nameof(ServerProxySettings.Image))?.GetMaxLength());
        Assert.Null(settings.FindProperty(nameof(ServerProxySettings.ServerId)));
        Assert.Contains(settings.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Server) &&
                          foreignKey.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Fact]
    public void Model_MapsDeploymentMvpMetadata()
    {
        using VesselDbContext context = CreateContext();

        IEntityType? deployment = context.Model.FindEntityType(typeof(Deployment));

        Assert.Equal(2048, deployment?.FindProperty(nameof(Deployment.RepositoryUrl))?.GetMaxLength());
        Assert.Equal(255, deployment?.FindProperty(nameof(Deployment.CommitBranch))?.GetMaxLength());
        Assert.Equal(512, deployment?.FindProperty(nameof(Deployment.ConfigurationSnapshotReference))?.GetMaxLength());
    }

    [Fact]
    public void Model_MapsPhase9WebhookAndPreviewTables()
    {
        using VesselDbContext context = CreateContext();

        IEntityType? webhookEvent = context.Model.FindEntityType(typeof(WebhookEvent));
        IEntityType? configuration = context.Model.FindEntityType(typeof(ApplicationWebhookConfiguration));
        IEntityType? preview = context.Model.FindEntityType(typeof(ApplicationPreview));

        Assert.Equal("webhook_events", webhookEvent?.GetTableName());
        Assert.Equal(512, webhookEvent?.FindProperty(nameof(WebhookEvent.DedupeKey))?.GetMaxLength());
        Assert.Equal("application_webhook_configurations", configuration?.GetTableName());
        Assert.Equal("application_previews", preview?.GetTableName());
        Assert.Equal(2048, preview?.FindProperty(nameof(ApplicationPreview.PullRequestUrl))?.GetMaxLength());
    }

    [Fact]
    public void Model_MapsPhase11ManagedServicesBackupsAndRestoreTables()
    {
        using VesselDbContext context = CreateContext();

        IEntityType? database = context.Model.FindEntityType(typeof(DatabaseResource));
        IEntityType? service = context.Model.FindEntityType(typeof(ServiceResource));
        IEntityType? schedule = context.Model.FindEntityType(typeof(BackupSchedule));
        IEntityType? execution = context.Model.FindEntityType(typeof(BackupExecution));

        Assert.Equal("database_resources", database?.GetTableName());
        Assert.Equal(32, database?.FindProperty(nameof(DatabaseResource.LifecycleState))?.GetMaxLength());
        Assert.Equal("service_resources", service?.GetTableName());
        Assert.Equal("jsonb", service?.FindProperty(nameof(ServiceResource.ConfigurationJson))?.GetColumnType());
        Assert.Equal("backup_schedules", schedule?.GetTableName());
        Assert.Equal(120, schedule?.FindProperty(nameof(BackupSchedule.CronExpression))?.GetMaxLength());
        Assert.Equal("backup_executions", execution?.GetTableName());
        Assert.Equal(512, execution?.FindProperty(nameof(BackupExecution.ArtifactKey))?.GetMaxLength());
        Assert.Equal(1000, execution?.FindProperty(nameof(BackupExecution.LastRestoreFailureReason))?.GetMaxLength());
    }

    [Fact]
    public void Model_MapsPerApplicationWebhookVerificationAndPreviewIsolation()
    {
        using VesselDbContext context = CreateContext();

        IEntityType webhookEvent = context.Model.FindEntityType(typeof(WebhookEvent))!;
        IProperty verified = webhookEvent.FindProperty(nameof(WebhookEvent.VerifiedApplicationIds))!;
        Assert.Equal("uuid[]", verified.GetColumnType());
        Assert.False(verified.IsNullable);

        IEntityType preview = context.Model.FindEntityType(typeof(ApplicationPreview))!;
        Assert.NotNull(preview.FindProperty(nameof(ApplicationPreview.SourceRepository)));
        Assert.NotNull(preview.FindProperty(nameof(ApplicationPreview.IsSameRepository)));

        // Index facets such as NULLS NOT DISTINCT are design-time annotations (used by migrations).
        IModel designModel = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        IEntityType variable = designModel.FindEntityType(typeof(EnvironmentVariable))!;
        IIndex unique = Assert.Single(variable.GetIndexes(), index => index.IsUnique);
        Assert.Contains(unique.Properties, property => property.Name == nameof(EnvironmentVariable.IsPreview));
        Assert.Equal(false, unique.FindAnnotation("Npgsql:NullsDistinct")?.Value);
    }

    [Fact]
    public void Model_MapsDatabaseRestoresAndBackupTimeout()
    {
        using VesselDbContext context = CreateContext();

        IEntityType? schedule = context.Model.FindEntityType(typeof(BackupSchedule));
        IEntityType? restore = context.Model.FindEntityType(typeof(DatabaseRestore));

        Assert.Equal(BackupSchedule.DefaultTimeoutSeconds,
            schedule?.FindProperty(nameof(BackupSchedule.TimeoutSeconds))?.GetDefaultValue());
        Assert.Equal("database_restores", restore?.GetTableName());
        Assert.Equal(32, restore?.FindProperty(nameof(DatabaseRestore.Status))?.GetMaxLength());
        Assert.Equal(DatabaseRestore.MaxLogLength, restore?.FindProperty(nameof(DatabaseRestore.Log))?.GetMaxLength());
        Assert.Null(restore?.FindProperty(nameof(DatabaseRestore.IsActive)));
        Assert.Null(context.Model.FindEntityType(typeof(BackupExecution))
            ?.FindProperty(nameof(BackupExecution.HasRestorableArtifact)));
    }

    [Fact]
    public void Model_StoresAccessGrantsAsJsonbAndTracksPausedTokens()
    {
        using VesselDbContext context = CreateContext();

        foreach (Type entity in new[]
                 {
                     typeof(TeamMembership), typeof(TeamInvitation), typeof(Vessel.Domain.Users.PersonalAccessToken)
                 })
        {
            IProperty? grants = context.Model.FindEntityType(entity)?.FindProperty("AccessGrants");
            Assert.NotNull(grants);
            Assert.Equal("jsonb", grants.GetColumnType());
            Assert.True(grants.IsNullable);
        }

        IProperty? pausedAt = context.Model.FindEntityType(typeof(Vessel.Domain.Users.PersonalAccessToken))
            ?.FindProperty(nameof(Vessel.Domain.Users.PersonalAccessToken.PausedAt));
        Assert.NotNull(pausedAt);
        Assert.True(pausedAt.IsNullable);
    }

    [Fact]
    public void AccessGrantColumns_RoundTripAndDropUnknownValues()
    {
        using VesselDbContext context = CreateContext();
        IProperty grants = context.Model.FindEntityType(typeof(TeamMembership))!.FindProperty("AccessGrants")!;
        var converter = grants.GetValueConverter()!;
        var projectId = Guid.NewGuid();
        IReadOnlyList<Vessel.Domain.Access.AccessGrant> value =
        [
            Vessel.Domain.Access.AccessGrant.Team(Vessel.Domain.Access.AccessLevel.Read),
            new(Vessel.Domain.Access.AccessScope.Project, projectId,
                [Vessel.Domain.Access.AccessLevel.Write, Vessel.Domain.Access.AccessLevel.Deploy])
        ];

        var json = (string)converter.ConvertToProvider(value)!;
        var restored = (IReadOnlyList<Vessel.Domain.Access.AccessGrant>)converter.ConvertFromProvider(json)!;
        var tampered = (IReadOnlyList<Vessel.Domain.Access.AccessGrant>)converter.ConvertFromProvider(
            """[{"scope":"galaxy","target":null,"levels":["read"]},{"scope":"team","levels":["read","sudo"]}]""")!;

        Assert.Contains("\"write\"", json, StringComparison.Ordinal);
        Assert.Equal(value, restored);
        Assert.Equal([Vessel.Domain.Access.AccessGrant.Team(Vessel.Domain.Access.AccessLevel.Read)], tampered);
    }

    private static VesselDbContext CreateContext()
    {
        DbContextOptions<VesselDbContext> options = new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=vessel_tests;Username=vessel;Password=vessel")
            .Options;

        return new VesselDbContext(options);
    }
}
