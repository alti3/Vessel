using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.Jobs;
using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.Processes;
using Vessel.Application.Realtime;
using Vessel.Application.ScheduledTasks;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Application.Settings;
using Vessel.Domain;
using Vessel.Domain.Scheduling;
using Vessel.Domain.Ssh;
using Vessel.UnitTests.Application.Backups;

namespace Vessel.UnitTests.Application.Scheduling;

/// <summary>Builds the ledger and scheduled-task services on top of the backup fixture (team, database, local server).</summary>
internal sealed class SchedulingFixture
{
    public SchedulingFixture()
    {
        B = new BackupFixture();
    }

    public BackupFixture B { get; }

    public FakeServerCommands Commands { get; } = new();

    public RecordingRealtime Realtime { get; } = new();

    public SystemSettingsStore Store => new(B.Db, B.Time);

    public SchedulingSettingsService Settings => new(B.Db, Store, B.Authorization, B.Audit);

    public ScheduledJobDispatcher Dispatcher(params IScheduledJobSource[] sources)
    {
        return new ScheduledJobDispatcher(B.Db, sources, B.Jobs, B.Locks, Settings, B.Authorization,
            B.Notifications, B.Time, NullLogger<ScheduledJobDispatcher>.Instance);
    }

    public ScheduledJobRunner Runner(params IScheduledJobSource[] sources)
    {
        return new ScheduledJobRunner(B.Db, sources, new FixedWorkerHost(), new SecretsRedactor(), B.Time,
            NullLogger<ScheduledJobRunner>.Instance);
    }

    public ScheduledTaskOwners Owners => new(B.Db, B.Authorization);

    public ScheduledTaskRunner TaskRunner()
    {
        return new ScheduledTaskRunner(B.Db, Owners, B.Authorization, Commands, B.Locks, B.Vault,
            new SecretsRedactor(), Realtime, B.Notifications, B.Audit, new FixedWorkerHost(), B.Time);
    }

    public ScheduledTaskService Tasks(params IScheduledJobSource[] sources)
    {
        return new ScheduledTaskService(B.Db, Owners, B.Authorization, Dispatcher(sources), Settings, B.Audit,
            B.Time);
    }
}

internal sealed class FixedWorkerHost : IWorkerHostIdentity
{
    public string WorkerHost => "worker-1";
}

/// <summary>Replaces the context's secret values with a marker (like the real redactor).</summary>
internal sealed class SecretsRedactor : ISecretRedactor
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

internal sealed class RecordingRealtime : IRealtimeNotifier
{
    public List<(RealtimeGroup Group, RealtimeMessage Message)> Published { get; } = [];

    public Task PublishAsync(RealtimeGroup group, RealtimeMessage message, CancellationToken cancellationToken = default)
    {
        Published.Add((group, message));
        return Task.CompletedTask;
    }
}

/// <summary>Answers <c>ps</c> with <see cref="Containers" /> and <c>exec</c> with <see cref="OutputLines" /> / <see cref="ExitCode" />.</summary>
internal sealed class FakeServerCommands : IServerCommandRunner
{
    public List<IReadOnlyList<string>> Runs { get; } = [];

    public List<IReadOnlyList<string>> Streams { get; } = [];

    /// <summary>Lines of <c>ps --format {{.Names}}\t{{service}}</c>.</summary>
    public List<string> Containers { get; } = [];

    public List<string> OutputLines { get; } = ["ok"];

    public int ExitCode { get; set; }

    public Task<ServerCommandResult> RunAsync(ServerExecutionTarget target, IReadOnlyList<string> arguments,
        TimeSpan timeout, string? standardInput = null, CancellationToken cancellationToken = default)
    {
        Runs.Add(arguments);
        return Task.FromResult(new ServerCommandResult(0, string.Join('\n', Containers), string.Empty));
    }

    public Task<ServerCommandResult> RunWithKeyAsync(ServerExecutionTarget target, SshKeyId keyId,
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public async IAsyncEnumerable<ProcessOutputLine> StreamAsync(ServerExecutionTarget target,
        IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout,
        IReadOnlyList<string>? secretValues = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Streams.Add(arguments);
        var sequence = 0;
        foreach (var line in OutputLines)
        {
            await Task.Yield();
            yield return new ProcessOutputLine(sequence++, DateTimeOffset.UnixEpoch, ProcessStreamKind.StandardOutput,
                line);
        }

        if (ExitCode != 0)
        {
            var command = new ProcessCommand(arguments[0], arguments.Skip(1).ToArray());
            throw new ProcessExecutionException(new ProcessResult(command,
                new ProcessExitInfo(ExitCode, false, false, null), 1, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, string.Empty, string.Empty));
        }
    }
}

/// <summary>A source with one schedule; records the runs it executes.</summary>
internal sealed class FakeSource(ScheduledJobKind kind, string cron, MissedRunPolicy? policy = null) : IScheduledJobSource
{
    public Guid ScheduleId { get; } = Guid.NewGuid();

    public TeamId? TeamId { get; init; }

    public DateTimeOffset ActiveSince { get; init; } = DateTimeOffset.MinValue;

    public List<ScheduledJobRun> Executed { get; } = [];

    public List<ScheduledJobRun> Interrupted { get; } = [];

    public Func<ScheduledJobRun, ScheduledJobOutcome>? Behavior { get; set; }

    public ScheduledJobKind Kind => kind;

    public IReadOnlyList<ScheduledJobDefinition> ListActiveSchedules()
    {
        return [new ScheduledJobDefinition(kind, ScheduleId, TeamId, "nightly", cron, null, policy, 300, ActiveSince)];
    }

    public Task<ScheduledJobOutcome> RunAsync(ScheduledJobRun run, CancellationToken cancellationToken)
    {
        Executed.Add(run);
        return Task.FromResult(Behavior?.Invoke(run) ?? ScheduledJobOutcome.Succeeded());
    }

    public Task OnInterruptedAsync(ScheduledJobRun run, CancellationToken cancellationToken)
    {
        Interrupted.Add(run);
        return Task.CompletedTask;
    }
}
