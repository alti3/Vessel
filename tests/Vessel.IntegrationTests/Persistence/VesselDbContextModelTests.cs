using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Registries;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;
using Vessel.Domain.Teams;
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
        Assert.Equal("environment_variables", context.Model.FindEntityType(typeof(EnvironmentVariable))?.GetTableName());
        Assert.Equal("secret_values", context.Model.FindEntityType(typeof(SecretValue))?.GetTableName());
        Assert.Equal("registry_credentials", context.Model.FindEntityType(typeof(RegistryCredential))?.GetTableName());
        Assert.Equal("server_status_snapshots", context.Model.FindEntityType(typeof(ServerStatusSnapshot))?.GetTableName());
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
        Assert.Contains("ConfigurationSnapshotReference", script, StringComparison.Ordinal);
        Assert.Contains("CancellationRequestedAt", script, StringComparison.Ordinal);
        Assert.Contains("WebhookEventId", script, StringComparison.Ordinal);
        Assert.Contains("PreviewId", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_MapsDeploymentMvpMetadata()
    {
        using VesselDbContext context = CreateContext();

        var deployment = context.Model.FindEntityType(typeof(Deployment));

        Assert.Equal(2048, deployment?.FindProperty(nameof(Deployment.RepositoryUrl))?.GetMaxLength());
        Assert.Equal(255, deployment?.FindProperty(nameof(Deployment.CommitBranch))?.GetMaxLength());
        Assert.Equal(512, deployment?.FindProperty(nameof(Deployment.ConfigurationSnapshotReference))?.GetMaxLength());
    }

    [Fact]
    public void Model_MapsPhase9WebhookAndPreviewTables()
    {
        using VesselDbContext context = CreateContext();

        var webhookEvent = context.Model.FindEntityType(typeof(WebhookEvent));
        var configuration = context.Model.FindEntityType(typeof(ApplicationWebhookConfiguration));
        var preview = context.Model.FindEntityType(typeof(ApplicationPreview));

        Assert.Equal("webhook_events", webhookEvent?.GetTableName());
        Assert.Equal(512, webhookEvent?.FindProperty(nameof(WebhookEvent.DedupeKey))?.GetMaxLength());
        Assert.Equal("application_webhook_configurations", configuration?.GetTableName());
        Assert.Equal("application_previews", preview?.GetTableName());
        Assert.Equal(2048, preview?.FindProperty(nameof(ApplicationPreview.PullRequestUrl))?.GetMaxLength());
    }

    private static VesselDbContext CreateContext()
    {
        DbContextOptions<VesselDbContext> options = new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=vessel_tests;Username=vessel;Password=vessel")
            .Options;

        return new VesselDbContext(options);
    }
}
