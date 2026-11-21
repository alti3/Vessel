using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Vessel.Application.Auditing;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Application.Notifications;
using Vessel.Application.Processes;
using Vessel.Application.Realtime;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Secrets;
using Vessel.Domain.Servers;

namespace Vessel.UnitTests.Servers;

internal sealed class FakeMonitoringRuntime : IContainerRuntimeClient
{
    public List<ContainerStateReading> Containers { get; } = [];

    public List<ImageDetails> Images { get; } = [];

    public Dictionary<string, List<string>> Logs { get; } = new(StringComparer.Ordinal);

    public List<string> Stopped { get; } = [];

    public List<string> RemovedImages { get; } = [];

    public List<string> RemovedContainers { get; } = [];

    public List<RuntimePruneKind> Prunes { get; } = [];

    public Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ContainerRuntimeInfo(target.Provider, "27", "1.47", "linux", "amd64"));
    }

    public Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(ContainerRuntimeTarget target, bool all,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ContainerSummary>>([]);
    }

    public Task<string> InspectContainerAsync(ContainerRuntimeTarget target, string containerId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult("{}");
    }

    public Task<IReadOnlyList<ImageSummary>> ListImagesAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ImageSummary>>([]);
    }

    public Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<NetworkSummary>>([]);
    }

    public Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<VolumeSummary>>([]);
    }

    public Task EnsureNetworkAsync(ContainerRuntimeTarget target, string name,
        IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<ProcessOutputLine> BuildImageAsync(ContainerRuntimeTarget target,
        DockerBuildCommand command, CancellationToken cancellationToken = default)
    {
        return AsyncEnumerable.Empty<ProcessOutputLine>();
    }

    public IAsyncEnumerable<ContainerEvent> StreamEventsAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        return AsyncEnumerable.Empty<ContainerEvent>();
    }

    public IAsyncEnumerable<ProcessOutputLine> RunComposeAsync(ContainerRuntimeTarget target, ComposeCommand command,
        CancellationToken cancellationToken = default)
    {
        return AsyncEnumerable.Empty<ProcessOutputLine>();
    }

    public Task<IReadOnlyList<ContainerStateReading>> ListContainerStatesAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ContainerStateReading>>(Containers.ToArray());
    }

    public Task<IReadOnlyList<ImageDetails>> ListImageDetailsAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ImageDetails>>(Images.ToArray());
    }

    public async IAsyncEnumerable<ProcessOutputLine> StreamContainerLogsAsync(ContainerRuntimeTarget target,
        string containerId, ContainerLogOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var sequence = 0L;
        foreach (var line in Logs.GetValueOrDefault(containerId) ?? [])
            yield return new ProcessOutputLine(sequence++, DateTimeOffset.UtcNow, ProcessStreamKind.StandardOutput,
                line);
    }

    public Task<bool> StopContainersAsync(ContainerRuntimeTarget target, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        Stopped.AddRange(containerIds);
        return Task.FromResult(true);
    }

    public Task<int> RemoveContainersAsync(ContainerRuntimeTarget target, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        RemovedContainers.AddRange(containerIds);
        return Task.FromResult(containerIds.Count);
    }

    public Task<bool> RemoveImageAsync(ContainerRuntimeTarget target, string imageReference,
        CancellationToken cancellationToken = default)
    {
        RemovedImages.Add(imageReference);
        return Task.FromResult(true);
    }

    public Task<RuntimePruneResult> PruneAsync(ContainerRuntimeTarget target, RuntimePruneKind kind,
        CancellationToken cancellationToken = default)
    {
        Prunes.Add(kind);
        return Task.FromResult(new RuntimePruneResult(true, 1000));
    }

    public static ContainerStateReading Container(string id, string state, IReadOnlyDictionary<string, string> labels,
        int restartCount = 0, string? health = null, string imageId = "sha256:img", string image = "img:1")
    {
        return new ContainerStateReading(id, id, image, imageId, state, health, restartCount, null, null, null,
            labels);
    }
}

internal sealed class RecordingJobs : IBackgroundJobDispatcher, IRecurringJobScheduler
{
    public List<string> Enqueued { get; } = [];

    public List<(string Job, TimeSpan Delay)> Scheduled { get; } = [];

    public List<string> Recurring { get; } = [];

    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall)
    {
        Enqueued.Add(typeof(TJob).Name);
        return "job-" + Enqueued.Count;
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay)
    {
        Scheduled.Add((typeof(TJob).Name, delay));
        return "scheduled-" + Scheduled.Count;
    }

    public void AddOrUpdate<TJob>(string recurringJobId, Expression<Func<TJob, Task>> methodCall,
        string cronExpression)
    {
        Recurring.Add(recurringJobId + " " + cronExpression);
    }

    public void RemoveIfExists(string recurringJobId)
    {
        Recurring.Add("removed " + recurringJobId);
    }
}

internal sealed class NullRealtime : IRealtimeNotifier
{
    public List<RealtimeMessage> Messages { get; } = [];

    public Task PublishAsync(RealtimeGroup group, RealtimeMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class NullAudit : IAuditWriter
{
    public List<string> Actions { get; } = [];

    public Task RecordAsync(TeamId? teamId, UserId? actorUserId, string action, AuditTarget target,
        string? correlationId, IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken = default)
    {
        Actions.Add(action);
        return Task.CompletedTask;
    }
}

internal sealed class FakeThrottle : INotificationThrottle
{
    public List<string> Released { get; } = [];

    public Task<bool> TryClaimAsync(TeamId teamId, string eventType, string subjectKey, TimeSpan? interval,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task<bool> ReleaseAsync(TeamId teamId, string eventType, string subjectKey,
        CancellationToken cancellationToken = default)
    {
        Released.Add(eventType);
        return Task.FromResult(true);
    }

    public Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }
}

/// <summary>Replaces every secret value with <c>&lt;REDACTED&gt;</c>.</summary>
internal sealed class ValueRedactor : ISecretRedactor
{
    public string Redact(string value, RedactionContext? context = null)
    {
        foreach (var secret in context?.SecretValues ?? [])
            if (!string.IsNullOrEmpty(secret))
                value = value.Replace(secret, "<REDACTED>", StringComparison.Ordinal);
        return value;
    }

    public byte[] RedactUtf8(byte[] value, RedactionContext? context = null)
    {
        return value;
    }
}

internal sealed class NoVault : ISecretVault
{
    public Task<SecretReference> StoreAsync(TeamId teamId, SecretScope scope, string key, string plaintext,
        SecretPolicy policy, SecretTarget target, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task ReplaceAsync(SecretReferenceId secretReferenceId, string plaintext,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<string> RevealAsync(UserId actorUserId, TeamId teamId, SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<string> RevealForDeploymentAsync(TeamId teamId, SecretReferenceId secretReferenceId,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}

internal sealed class FakeTelemetryCollector : IServerTelemetryCollector
{
    public ServerTelemetry? Next { get; set; }

    public bool Fail { get; set; }

    public int Calls { get; private set; }

    public Task<ServerTelemetry> CollectAsync(Server server, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Fail) throw new InvalidOperationException("ssh failed");
        return Task.FromResult(Next ?? throw new InvalidOperationException("no telemetry"));
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }
}
