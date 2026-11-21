using Vessel.Application.Common;
using Vessel.Application.ManagedServices;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Application;

/// <summary>DATA-04 (health checks), DATA-05 (.env quoting), DATA-11 / PLT-06 (pinned images).</summary>
public sealed class DatabaseProvisioningTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pa$word", "\"pa$$word\"")]
    [InlineData("${HOME}", "\"$${HOME}\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("hash # not a comment", "\"hash # not a comment\"")]
    [InlineData("line\nbreak", "\"line\\nbreak\"")]
    [InlineData("", "\"\"")]
    public void EnvFile_QuotesEveryValueSoComposeReadsItLiterally(string value, string expected)
    {
        Assert.Equal(expected, ComposeEnvironmentFile.Quote(value));
    }

    [Fact]
    public void EnvFile_RefusesValuesEndingInABackslash()
    {
        Assert.Throws<ArgumentException>(() => ComposeEnvironmentFile.Quote("ends\\"));
    }

    [Fact]
    public void EnvFile_WritesSortedNormalizedKeys()
    {
        var contents = ComposeEnvironmentFile.Format(new Dictionary<string, string>
        {
            ["REDIS_PASSWORD"] = "a$b",
            ["APP_KEY"] = "x"
        });

        Assert.Equal("APP_KEY=\"x\"\nREDIS_PASSWORD=\"a$$b\"\n", contents);
    }

    [Fact]
    public void Compose_AddsEngineHealthCheckWithoutPuttingThePasswordInIt()
    {
        DatabaseResource database = CreateDatabase(DatabaseEngine.PostgreSql, DatabaseImage.Parse("postgres:18-alpine"));

        DatabaseProvisioningPlan plan = DatabaseComposeBuilder.CreatePlan(database, "pa$$word-secret");

        Assert.Contains("image: \"postgres:18-alpine\"", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_PASSWORD: \"${POSTGRES_PASSWORD}\"", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("healthcheck:", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("PGPASSWORD=\\\"$$POSTGRES_PASSWORD\\\" psql", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("interval: 15s", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("timeout: 5s", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("retries: 5", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("start_period: 5s", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Equal("pa$$word-secret", plan.SecretValues["POSTGRES_PASSWORD"]);
    }

    [Theory]
    [InlineData(DatabaseEngine.MySql, "mysqladmin ping")]
    [InlineData(DatabaseEngine.MariaDb, "healthcheck.sh --connect --innodb_initialized")]
    [InlineData(DatabaseEngine.Redis, "redis-cli --no-auth-warning ping | grep -q PONG")]
    [InlineData(DatabaseEngine.KeyDb, "keydb-cli --no-auth-warning ping | grep -q PONG")]
    [InlineData(DatabaseEngine.Dragonfly, "redis-cli --no-auth-warning ping | grep -q PONG")]
    [InlineData(DatabaseEngine.MongoDb, "mongosh --quiet")]
    [InlineData(DatabaseEngine.ClickHouse, "clickhouse-client")]
    public void Compose_UsesTheEngineProbe(DatabaseEngine engine, string probe)
    {
        DatabaseResource database = CreateDatabase(engine,
            DatabaseImage.Parse(DatabaseEngineProfiles.DefaultImage(engine)));

        DatabaseProvisioningPlan plan = DatabaseComposeBuilder.CreatePlan(database, "secret");

        Assert.Contains(probe, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"secret\"", plan.ComposeYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_DisablesTheHealthCheckWhenTurnedOff()
    {
        DatabaseResource database = CreateDatabase(DatabaseEngine.Redis, DatabaseImage.Parse(ComponentVersions.ValkeyImage));
        database.ChangeHealthCheck(new DatabaseHealthCheckPolicy(false, 15, 5, 5, 5), Now);

        DatabaseProvisioningPlan plan = DatabaseComposeBuilder.CreatePlan(database, "secret");

        Assert.Contains("disable: true", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("command: [\"redis-server\", \"--requirepass\", \"${REDIS_PASSWORD}\"]", plan.ComposeYaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_KeepsTheLegacyImageOfDatabasesCreatedBeforePinning()
    {
        DatabaseResource database = CreateDatabase(DatabaseEngine.PostgreSql, null, "16");

        Assert.Equal("postgres:16", DatabaseEngineProfiles.EffectiveImage(database));
    }

    [Fact]
    public void DefaultImages_MatchUpstreamDefaults()
    {
        Assert.Equal("postgres:18-alpine", DatabaseEngineProfiles.DefaultImage(DatabaseEngine.PostgreSql));
        Assert.Equal("valkey/valkey:9-alpine", DatabaseEngineProfiles.DefaultImage(DatabaseEngine.Redis));
        Assert.Equal("redis:8", DatabaseEngineProfiles.DefaultImage(DatabaseEngine.Redis, RedisFlavor.Redis));
        Assert.Equal("mongo:9", DatabaseEngineProfiles.DefaultImage(DatabaseEngine.MongoDb));
        Assert.Equal("mysql:9.7", DatabaseEngineProfiles.DefaultImage(DatabaseEngine.MySql));
        Assert.Equal("mariadb:12.3", DatabaseEngineProfiles.DefaultImage(DatabaseEngine.MariaDb));
        Assert.Equal("clickhouse/clickhouse-server:26.8", DatabaseEngineProfiles.DefaultImage(DatabaseEngine.ClickHouse));
        foreach (DatabaseEngineProfile profile in DatabaseEngineProfiles.All)
            Assert.NotEqual("latest", DatabaseImage.Parse(profile.DefaultImage).Tag);
    }

    [Fact]
    public void NewImages_RejectLatestAndResolveVersionsAgainstTheEngineRepository()
    {
        Assert.Throws<DomainException>(() =>
            DatabaseEngineProfiles.ResolveNewImage(DatabaseEngine.PostgreSql, "postgres:latest", null));
        Assert.Throws<DomainException>(() =>
            DatabaseEngineProfiles.ResolveNewImage(DatabaseEngine.PostgreSql, null, "latest"));
        Assert.Equal("postgres:16-alpine",
            DatabaseEngineProfiles.ResolveNewImage(DatabaseEngine.PostgreSql, null, "16-alpine").Value);
        Assert.Equal("valkey/valkey:8.1",
            DatabaseEngineProfiles.ResolveNewImage(DatabaseEngine.Redis, null, "8.1").Value);
        Assert.Equal(ComponentVersions.SupabasePostgresImage,
            DatabaseEngineProfiles.ResolveNewImage(DatabaseEngine.PostgreSql, ComponentVersions.SupabasePostgresImage,
                null).Value);
    }

    [Fact]
    public void PostgresMountPath_FollowsTheMajorVersion()
    {
        Assert.Equal("/var/lib/postgresql",
            DatabaseEngineProfiles.DefaultMountPath(DatabaseEngine.PostgreSql, DatabaseImage.Parse("postgres:18-alpine")));
        Assert.Equal("/var/lib/postgresql/data",
            DatabaseEngineProfiles.DefaultMountPath(DatabaseEngine.PostgreSql, DatabaseImage.Parse("postgres:16")));
        Assert.Equal("/var/lib/mysql",
            DatabaseEngineProfiles.DefaultMountPath(DatabaseEngine.MariaDb, DatabaseImage.Parse("mariadb:12.3")));
    }

    [Theory]
    [InlineData("""[{"State":{"Running":true,"Health":{"Status":"healthy"}}}]""", true, DatabaseHealthState.Healthy)]
    [InlineData("""[{"State":{"Running":true,"Health":{"Status":"starting"}}}]""", true, DatabaseHealthState.Starting)]
    [InlineData("""[{"State":{"Running":true,"Health":{"Status":"unhealthy"}}}]""", true, DatabaseHealthState.Unhealthy)]
    [InlineData("""[{"State":{"Running":true,"Healthcheck":{"Status":"healthy"}}}]""", true, DatabaseHealthState.Healthy)]
    [InlineData("""[{"State":{"Running":false,"Health":{"Status":"healthy"}}}]""", true, DatabaseHealthState.Unhealthy)]
    [InlineData("""[{"State":{"Running":true}}]""", true, DatabaseHealthState.Unknown)]
    [InlineData("""[{"State":{"Running":true,"Health":{"Status":"healthy"}}}]""", false, DatabaseHealthState.Unknown)]
    [InlineData("""[]""", true, DatabaseHealthState.Unhealthy)]
    [InlineData("""not json""", true, DatabaseHealthState.Unknown)]
    public void HealthProbe_MapsRuntimeState(string inspect, bool healthCheckEnabled, DatabaseHealthState expected)
    {
        Assert.Equal(expected, DatabaseHealthProbe.Map(inspect, healthCheckEnabled));
    }

    [Theory]
    [InlineData("keydb")]
    [InlineData("valkey")]
    [InlineData("dragonfly")]
    [InlineData("cloudflared")]
    [InlineData("postgres-timescale")]
    [InlineData("qdrant")]
    [InlineData("chromadb")]
    public void DatabaseAndTunnelTemplates_ArePinned(string templateKey)
    {
        var catalog = new ServiceTemplateCatalog();
        ServiceTemplateSummary template = catalog.List().Single(item => item.Key == templateKey);
        Dictionary<string, string> inputs = template.Inputs.ToDictionary(input => input.Key,
            input => input.DefaultValue ?? "value123");

        ServiceProvisioningPlan plan = catalog.CreatePlan(templateKey, Guid.NewGuid(), "svc", inputs);

        Assert.DoesNotContain(":latest", plan.ComposeYaml, StringComparison.Ordinal);
    }

    private static DatabaseResource CreateDatabase(DatabaseEngine engine, DatabaseImage? image, string version = "1")
    {
        return DatabaseResource.Create(EnvironmentId.New(), ServerId.New(), new ResourceName("main"), engine,
            new VersionLabel(image?.Tag ?? version), new StorageConfiguration("data", "/data"), SecretReferenceId.New(),
            Now, image);
    }
}
