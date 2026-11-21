using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;
using Vessel.Infrastructure.Docker;
using Vessel.Infrastructure.Monitoring;
using Vessel.Infrastructure.Persistence;

namespace Vessel.IntegrationTests.Monitoring;

/// <summary>SRV-03 agentless telemetry parsing and the monitoring persistence model.</summary>
public sealed class TelemetryParserTests
{
    private const string Output = """
                                  @@stat1
                                  cpu  100 0 100 800 0 0 0 0 0 0
                                  @@uptime1
                                  1000.00 3000.00
                                  @@net1
                                  Inter-|   Receive                                                |  Transmit
                                   face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                                      lo: 5000 10 0 0 0 0 0 0 5000 10 0 0 0 0 0 0
                                    eth0: 1000 10 0 0 0 0 0 0 2000 10 0 0 0 0 0 0
                                  veth12: 9999 10 0 0 0 0 0 0 9999 10 0 0 0 0 0 0
                                  @@stat2
                                  cpu  150 0 150 900 0 0 0 0 0 0
                                  @@uptime2
                                  1002.00 3004.00
                                  @@net2
                                    eth0: 3000 10 0 0 0 0 0 0 6000 10 0 0 0 0 0 0
                                  veth12: 99999 10 0 0 0 0 0 0 99999 10 0 0 0 0 0 0
                                  @@meminfo
                                  MemTotal:        2000000 kB
                                  MemFree:          500000 kB
                                  MemAvailable:    1500000 kB
                                  @@loadavg
                                  0.52 0.58 0.59 1/467 12345
                                  @@nproc
                                  4
                                  @@df
                                  Filesystem     1024-blocks    Used Available Capacity Mounted on
                                  /dev/sda1        1000000  850000    150000      85% /
                                  /dev/sda1        1000000  850000    150000      85% /
                                  /dev/sdb1        2000000  200000   1800000      10% /var/lib/docker
                                  @@stats
                                  {"id":"abc123","name":"web","cpu":"12.50%","mem":"10.5MiB / 1.944GiB","net":"1.2kB / 3MB"}
                                  not json
                                  @@inspect
                                  @@inventory
                                  {"id":"abc123def","name":"/web","image":"nginx:1","imageId":"sha256:1","state":"running","health":"healthy","restartCount":2,"exitCode":0,"startedAt":"2026-11-21T10:00:00.123456789Z","finishedAt":"0001-01-01T00:00:00Z","labels":{"vessel.application":"x"}}
                                  @@end
                                  """;

    [Fact]
    public void ParsesAFullCollection()
    {
        var sections = TelemetryParsers.Sections(Output);
        ServerTelemetry telemetry = AgentlessTelemetryCollector.Parse(sections, DateTimeOffset.UnixEpoch);

        HostMetricsReading host = Assert.IsType<HostMetricsReading>(telemetry.Host);
        // Δtotal = 200, Δidle = 100 → 50 %.
        Assert.Equal(50m, host.CpuPercent);
        Assert.Equal(4, host.CpuCount);
        Assert.Equal(0.52m, host.Load1);
        Assert.Equal(2000000L * 1024, host.MemoryTotalBytes);
        Assert.Equal(500000L * 1024, host.MemoryUsedBytes);
        // eth0 only (lo and veth skipped), over the 2 s measured by /proc/uptime.
        Assert.Equal(1000, host.NetworkRxBytesPerSecond);
        Assert.Equal(2000, host.NetworkTxBytesPerSecond);
        Assert.Equal(2, host.Disks.Count);
        Assert.Equal("/", host.PrimaryDisk!.MountPoint);
        Assert.Equal(85m, host.PrimaryDisk.UsedPercent);

        ContainerStatsReading stats = Assert.Single(telemetry.Stats);
        Assert.Equal(12.5m, stats.CpuPercent);
        Assert.Equal((long)(10.5m * 1_048_576m), stats.MemoryUsedBytes);
        Assert.Equal(1200, stats.NetworkRxBytes);
        Assert.Equal(3_000_000, stats.NetworkTxBytes);

        Assert.True(telemetry.ContainersComplete);
        var container = Assert.Single(telemetry.Containers);
        Assert.Equal("web", container.Name);
        Assert.Equal(2, container.RestartCount);
        Assert.Equal("healthy", container.Health);
        Assert.Null(container.FinishedAt);
        Assert.NotNull(container.StartedAt);
        Assert.Equal("x", container.Labels["vessel.application"]);
    }

    [Fact]
    public void AMissingInventoryIsAnIncompleteSnapshot()
    {
        ServerTelemetry telemetry = AgentlessTelemetryCollector.Parse(
            TelemetryParsers.Sections("@@stats\n@@inspect\n@@end\n"), DateTimeOffset.UnixEpoch);
        Assert.False(telemetry.ContainersComplete);
        Assert.Empty(telemetry.Containers);
        Assert.Null(telemetry.Host);
    }

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("12kB", 12_000L)]
    [InlineData("1.5GiB", 1_610_612_736L)]
    [InlineData("3 MB", 3_000_000L)]
    [InlineData("--", null)]
    [InlineData("12XB", null)]
    public void ParsesDockerSizes(string value, long? expected)
    {
        Assert.Equal(expected, TelemetryParsers.ParseSize(value));
    }

    [Fact]
    public void ParsesReclaimedSpaceAndImageDetails()
    {
        Assert.Equal(1_200_000_000, TelemetryParsers.ParseReclaimedSpace("Deleted: x\nTotal reclaimed space: 1.2GB\n"));
        Assert.Equal(0, TelemetryParsers.ParseReclaimedSpace("nothing"));
        var images = DockerCliContainerRuntimeClient.ParseImageDetails(
            "{\"id\":\"sha256:a\",\"tags\":[\"nginx:1\"],\"size\":1234,\"created\":\"2026-01-01T00:00:00Z\",\"labels\":null}\n");
        var image = Assert.Single(images);
        Assert.Equal(["nginx:1"], image.RepoTags);
        Assert.Equal(1234, image.SizeBytes);
    }

    [Fact]
    public async Task CollectorPassesTheRuntimeAndTemplatesAsPositionalArguments()
    {
        var commands = new RecordingCommands(Output);
        var collector = new AgentlessTelemetryCollector(commands, TimeProvider.System);
        Server server = Server.Create(TeamId.New(), new ResourceName("s"),
            new ServerAddress("10.0.0.2", new PortNumber(22)), ServerConnectionType.Ssh, ContainerRuntimeKind.Podman,
            DateTimeOffset.UtcNow);

        ServerTelemetry telemetry = await collector.CollectAsync(server, TestContext.Current.CancellationToken);

        Assert.NotNull(telemetry.Host);
        Assert.Equal(["sh", "-c", AgentlessTelemetryCollector.Script, "vessel", "podman",
            AgentlessTelemetryCollector.InspectFormat, AgentlessTelemetryCollector.StatsFormat], commands.Arguments);
        Assert.True(commands.Target!.IsRemote);
        // The inspect template never reads environment variables or commands.
        Assert.DoesNotContain(".Config.Env", AgentlessTelemetryCollector.InspectFormat, StringComparison.Ordinal);
        Assert.DoesNotContain(".Config.Cmd", AgentlessTelemetryCollector.InspectFormat, StringComparison.Ordinal);

        commands.Output = "partial output";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collector.CollectAsync(server, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ModelMapsMonitoringTables()
    {
        DbContextOptions<VesselDbContext> options = new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql("Host=localhost;Database=vessel_model_only")
            .Options;
        using var context = new VesselDbContext(options);
        Assert.Equal("server_metric_samples", context.Model.FindEntityType(typeof(ServerMetricSample))?.GetTableName());
        Assert.Equal("container_metric_samples",
            context.Model.FindEntityType(typeof(ContainerMetricSample))?.GetTableName());
        Assert.Equal("server_monitoring_settings",
            context.Model.FindEntityType(typeof(ServerMonitoringSettings))?.GetTableName());
        Assert.Equal("resource_runtime_statuses",
            context.Model.FindEntityType(typeof(ResourceRuntimeStatus))?.GetTableName());
        Assert.Equal("docker_cleanup_executions",
            context.Model.FindEntityType(typeof(DockerCleanupExecution))?.GetTableName());
        var script = context.Database.GenerateCreateScript();
        Assert.Contains("docker_cleanup_delete_unused_volumes", script, StringComparison.Ordinal);
        Assert.Contains("ConsecutiveFailedHealthChecks", script, StringComparison.Ordinal);
    }

    private sealed class RecordingCommands(string output) : IServerCommandRunner
    {
        public string Output { get; set; } = output;

        public IReadOnlyList<string>? Arguments { get; private set; }

        public ServerExecutionTarget? Target { get; private set; }

        public Task<ServerCommandResult> RunAsync(ServerExecutionTarget target, IReadOnlyList<string> arguments,
            TimeSpan timeout, string? standardInput = null, CancellationToken cancellationToken = default)
        {
            Target = target;
            Arguments = arguments;
            return Task.FromResult(new ServerCommandResult(0, Output, string.Empty));
        }

        public Task<ServerCommandResult> RunWithKeyAsync(ServerExecutionTarget target, SshKeyId keyId,
            IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<ProcessOutputLine> StreamAsync(ServerExecutionTarget target,
            IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout,
            IReadOnlyList<string>? secretValues = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
