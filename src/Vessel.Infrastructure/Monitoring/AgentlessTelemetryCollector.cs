using Vessel.Application.Docker;
using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Application.Servers;
using Vessel.Domain.Servers;

namespace Vessel.Infrastructure.Monitoring;

/// <summary>
///     Agentless telemetry (ADR-0009): one read-only shell command per collection, run through
///     <see cref="IServerCommandRunner" /> on the server's own connection. The script is a constant; the runtime CLI and
///     the Go templates are passed as positional arguments (<c>$1</c>..<c>$3</c>), never interpolated. The container
///     inspect template reads identity, image, labels and state only (no environment, command or mounts), so secrets
///     never leave the host. CPU and network rates come from two <c>/proc</c> readings one second apart.
/// </summary>
public sealed class AgentlessTelemetryCollector(IServerCommandRunner commands, TimeProvider timeProvider)
    : IServerTelemetryCollector
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Probed mount points: root, the runtime data roots and the Vessel data directory (fallbacks like upstream).</summary>
    public const string Script =
        "echo @@stat1; head -n 1 /proc/stat; echo @@uptime1; cat /proc/uptime; echo @@net1; cat /proc/net/dev; " +
        "sleep 1; " +
        "echo @@stat2; head -n 1 /proc/stat; echo @@uptime2; cat /proc/uptime; echo @@net2; cat /proc/net/dev; " +
        "echo @@meminfo; cat /proc/meminfo; echo @@loadavg; cat /proc/loadavg; " +
        "echo @@nproc; nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo; " +
        "echo @@df; df -Pk / /var/lib/docker /var/lib/containers /var/lib/vessel /data 2>/dev/null; " +
        "echo @@stats; \"$1\" stats --no-stream --format \"$3\" 2>/dev/null; " +
        "echo @@inspect; ids=$(\"$1\" ps -aq --no-trunc 2>/dev/null) && { echo @@inventory; " +
        "if [ -n \"$ids\" ]; then \"$1\" inspect --format \"$2\" $ids 2>/dev/null; fi; }; " +
        "echo @@end";

    public const string InspectFormat =
        "{\"id\":{{json .Id}},\"name\":{{json .Name}},\"image\":{{json .Config.Image}},\"imageId\":{{json .Image}}," +
        "\"state\":{{json .State.Status}},\"health\":{{if .State.Health}}{{json .State.Health.Status}}{{else}}\"\"{{end}}," +
        "\"restartCount\":{{.RestartCount}},\"exitCode\":{{.State.ExitCode}},\"startedAt\":{{json .State.StartedAt}}," +
        "\"finishedAt\":{{json .State.FinishedAt}},\"labels\":{{json .Config.Labels}}}";

    public const string StatsFormat =
        "{\"id\":\"{{.ID}}\",\"name\":\"{{.Name}}\",\"cpu\":\"{{.CPUPerc}}\",\"mem\":\"{{.MemUsage}}\",\"net\":\"{{.NetIO}}\"}";

    public async Task<ServerTelemetry> CollectAsync(Server server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var cli = server.Runtime == ContainerRuntimeKind.Podman ? "podman" : "docker";
        ServerCommandResult result = await commands.RunAsync(ServerExecutionTarget.From(server),
            ["sh", "-c", Script, "vessel", cli, InspectFormat, StatsFormat], CommandTimeout, null, cancellationToken);
        var sections = TelemetryParsers.Sections(result.StandardOutput);
        if (!sections.ContainsKey("end"))
            throw new InvalidOperationException("The telemetry command did not complete on the server.");
        return Parse(sections, timeProvider.GetUtcNow());
    }

    public static ServerTelemetry Parse(IReadOnlyDictionary<string, string> sections, DateTimeOffset collectedAt)
    {
        string? Section(string name)
        {
            return sections.TryGetValue(name, out var value) ? value : null;
        }

        var cpu = TelemetryParsers.CpuPercent(TelemetryParsers.ParseCpuTimes(Section("stat1")),
            TelemetryParsers.ParseCpuTimes(Section("stat2")));
        var memory = TelemetryParsers.ParseMemory(Section("meminfo"));
        var load = TelemetryParsers.ParseLoad(Section("loadavg"));
        var elapsed = TelemetryParsers.ParseUptimeSeconds(Section("uptime2")) -
                      TelemetryParsers.ParseUptimeSeconds(Section("uptime1"));
        var net1 = TelemetryParsers.ParseNetwork(Section("net1"));
        var net2 = TelemetryParsers.ParseNetwork(Section("net2"));
        var disks = TelemetryParsers.ParseDisks(Section("df"));
        var host = new HostMetricsReading(
            cpu,
            TelemetryParsers.ParseCpuCount(Section("nproc")),
            load?.Load1,
            load?.Load5,
            load?.Load15,
            memory?.Total,
            memory?.Used,
            TelemetryParsers.Rate(net1?.Rx, net2?.Rx, elapsed),
            TelemetryParsers.Rate(net1?.Tx, net2?.Tx, elapsed),
            disks);
        var hostKnown = cpu.HasValue || memory.HasValue || load.HasValue || disks.Count > 0;
        var complete = sections.ContainsKey("inventory");
        return new ServerTelemetry(
            collectedAt,
            hostKnown ? host : null,
            TelemetryParsers.ParseStats(Section("stats")),
            complete ? TelemetryParsers.ParseContainers(Section("inventory")) : [],
            complete);
    }
}
