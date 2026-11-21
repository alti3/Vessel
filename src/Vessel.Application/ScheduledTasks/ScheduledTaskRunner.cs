using System.Globalization;
using System.Text;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Realtime;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Databases;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Notifications;
using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Scheduling;
using Vessel.Domain.Servers;
using ApplicationId = Vessel.Domain.ApplicationId;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.Application.ScheduledTasks;

/// <summary>
///     Runs scheduled tasks for the scheduled job ledger (NTF-07). One run: resolve the resource's running containers
///     on its server, pick the task's container, execute <c>&lt;runtime&gt; exec &lt;container&gt; sh -c &lt;command&gt;</c>
///     (or the split argument vector without a shell) through <see cref="IServerCommandRunner" /> as structured
///     arguments, stream redacted output into the execution record and the realtime channel, and track failures.
///     <list type="bullet">
///         <item>A stopped resource is skipped: no notification, no failure count (upstream v4.5.0/v4.7.0).</item>
///         <item>A run while the previous run of the task is still active is skipped (per-task lock).</item>
///         <item>
///             A failure notifies (throttled daily per task); ten failures in a row over at least 24 hours disable the
///             task; the first success after failures sends one "works again" notice.
///         </item>
///         <item>Tasks on host-local servers run only while the owning team includes an instance administrator.</item>
///     </list>
/// </summary>
public sealed class ScheduledTaskRunner(
    IVesselDbContext dbContext,
    ScheduledTaskOwners owners,
    VesselAuthorizationService authorization,
    IServerCommandRunner commands,
    IDistributedLockManager locks,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IRealtimeNotifier realtime,
    INotificationEventPublisher notifications,
    IAuditWriter auditWriter,
    IWorkerHostIdentity workerHost,
    TimeProvider timeProvider) : IScheduledJobSource
{
    public const string RealtimeOutputEvent = "scheduled_task.output";

    public const string RealtimeStatusEvent = "scheduled_task.status";

    private static readonly TimeSpan ContainerLookupTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LockMargin = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    public ScheduledJobKind Kind => ScheduledJobKind.ScheduledTask;

    public static string LockKey(ScheduledTaskId taskId)
    {
        return $"scheduled-task:{taskId.Value:D}";
    }

    public IReadOnlyList<ScheduledJobDefinition> ListActiveSchedules()
    {
        return dbContext.ScheduledTasks
            .Where(task => task.Enabled)
            .AsEnumerable()
            .Select(task => new ScheduledJobDefinition(Kind, task.Id.Value, task.TeamId, task.Name,
                task.CronExpression, task.TimeZone, task.MissedRunPolicy, task.TimeoutSeconds + 60,
                task.CreatedAt))
            .ToArray();
    }

    public async Task<ScheduledJobOutcome> RunAsync(ScheduledJobRun run, CancellationToken cancellationToken)
    {
        var taskId = new ScheduledTaskId(run.ScheduleId);
        ScheduledTask? task = await dbContext.ScheduledTaskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task is null) return ScheduledJobOutcome.Skipped("The scheduled task was deleted.");
        if (!task.Enabled && run.Trigger != ScheduledJobTrigger.Manual)
            return ScheduledJobOutcome.Skipped("The scheduled task is disabled.");

        var execution = ScheduledTaskExecution.Start(task.Id, task.TeamId, run.Id, run.Trigger,
            workerHost.WorkerHost, Now());
        await dbContext.ScheduledTaskExecutionRepository.AddAsync(execution, cancellationToken);
        task.RecordRun(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(execution, cancellationToken);

        ScheduledTaskOwner? owner = owners.Find(task.OwnerKind, task.OwnerId);
        Server? server = owner is null ? null : owners.FindServer(owner);
        if (owner is null || server is null || owner.TeamId != task.TeamId)
            return await SkipAsync(execution, "Skipped: the task's resource or server no longer exists.",
                cancellationToken);
        if (!HostLocalExecutionGuard.IsAuthorized(authorization, server, null))
            return await FailAsync(task, owner, execution, null, HostLocalExecutionGuard.UnattendedNotAuthorizedMessage,
                cancellationToken);

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(LockKey(task.Id),
            TimeSpan.FromSeconds(task.TimeoutSeconds) + LockMargin, TimeSpan.Zero, cancellationToken);
        if (handle is null)
            return await SkipAsync(execution, "Skipped: the previous run of this task is still running.",
                cancellationToken);

        string container;
        try
        {
            var resolved = await ResolveContainerAsync(task, owner, server, cancellationToken);
            if (resolved.Container is null)
                return resolved.SkipReason is not null
                    ? await SkipAsync(execution, resolved.SkipReason, cancellationToken)
                    : await FailAsync(task, owner, execution, null, resolved.Error!, cancellationToken);
            container = resolved.Container;
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(task, owner, execution, null,
                "Could not list the resource's containers: " + redactor.Redact(exception.Message), cancellationToken);
        }

        execution.UseContainer(container);
        IReadOnlyList<string> secrets = await CollectSecretsAsync(task, owner, cancellationToken);
        var context = new RedactionContext(secrets, null, null, true);
        IReadOnlyList<string> arguments = BuildArguments(server, container, task);
        return await ExecuteAsync(task, owner, execution, server, arguments, secrets, context, cancellationToken);
    }

    public async Task OnInterruptedAsync(ScheduledJobRun run, CancellationToken cancellationToken)
    {
        foreach (ScheduledTaskExecution execution in dbContext.ScheduledTaskExecutions
                     .Where(execution => execution.RunId == run.Id &&
                                         execution.Status == ScheduledTaskExecutionStatus.Running)
                     .ToArray())
            execution.Fail(null, "The worker stopped while this task was running. The task is not run again.",
                Now());
        await Task.CompletedTask;
    }

    /// <summary>
    ///     The structured argument vector: <c>docker exec &lt;container&gt; sh -c &lt;command&gt;</c> or, without a shell,
    ///     the command split into arguments. The command is never interpolated into a host shell string.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(Server server, string container, ScheduledTask task)
    {
        var arguments = new List<string> { RuntimeCli(server), "exec", container };
        if (task.UseShell)
        {
            arguments.AddRange(["sh", "-c", task.Command]);
        }
        else
        {
            IReadOnlyList<string> split = ScheduledTaskCommandLine.Split(task.Command);
            if (split.Count == 0) throw new InvalidOperationException("The command is empty.");
            arguments.AddRange(split);
        }

        return arguments;
    }

    private async Task<ScheduledJobOutcome> ExecuteAsync(ScheduledTask task, ScheduledTaskOwner owner,
        ScheduledTaskExecution execution, Server server, IReadOnlyList<string> arguments,
        IReadOnlyList<string> secrets, RedactionContext context, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(task.TimeoutSeconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout + TimeSpan.FromSeconds(5));
        var pending = new StringBuilder();
        DateTimeOffset lastFlush = Now();
        try
        {
            await foreach (ProcessOutputLine line in commands.StreamAsync(ServerExecutionTarget.From(server),
                               arguments, null, timeout, secrets, deadline.Token))
            {
                var redacted = redactor.Redact(line.Content, context);
                if (pending.Length > 0) pending.Append('\n');
                pending.Append(redacted);
                await PublishOutputAsync(execution, redacted, line.Stream, cancellationToken);
                if (Now() - lastFlush >= FlushInterval || pending.Length > 16 * 1024)
                {
                    await FlushAsync(execution, pending);
                    lastFlush = Now();
                }
            }

            await FlushAsync(execution, pending);
            execution.Succeed(0, Now());
            var recovered = task.RecordSuccess(Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await PublishStatusAsync(execution, CancellationToken.None);
            await notifications.PublishAsync(task.TeamId, ScheduledTaskNotifications.Succeeded(task, owner),
                CancellationToken.None);
            if (recovered)
                await notifications.PublishAsync(task.TeamId, ScheduledTaskNotifications.Recovered(task, owner),
                    CancellationToken.None);
            return ScheduledJobOutcome.Succeeded(null, execution.Id.Value);
        }
        catch (ProcessExecutionException exception)
        {
            await FlushAsync(execution, pending);
            var exitCode = exception.Result.ExitInfo.ExitCode;
            var reason = exception.Result.ExitInfo.TimedOut
                ? $"The task exceeded its timeout of {task.TimeoutSeconds} seconds and was stopped."
                : $"The command exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.";
            return await FailAsync(task, owner, execution, exitCode, reason, CancellationToken.None);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await FlushAsync(execution, pending);
            return await FailAsync(task, owner, execution, null,
                $"The task exceeded its timeout of {task.TimeoutSeconds} seconds and was stopped.",
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await FlushAsync(execution, pending);
            await FailAsync(task, owner, execution, null,
                "The control plane stopped while this task was running. The task is not run again.",
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await FlushAsync(execution, pending);
            return await FailAsync(task, owner, execution, null,
                "The task could not be run: " + redactor.Redact(exception.Message, context), CancellationToken.None);
        }
    }

    private async Task FlushAsync(ScheduledTaskExecution execution, StringBuilder pending)
    {
        if (pending.Length == 0) return;
        execution.AppendOutput(pending.ToString());
        pending.Clear();
        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ScheduledJobOutcome> SkipAsync(ScheduledTaskExecution execution, string reason,
        CancellationToken cancellationToken)
    {
        execution.Skip(reason, Now());
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await PublishStatusAsync(execution, cancellationToken);
        return ScheduledJobOutcome.Skipped(reason, execution.Id.Value);
    }

    private async Task<ScheduledJobOutcome> FailAsync(ScheduledTask task, ScheduledTaskOwner? owner,
        ScheduledTaskExecution execution, int? exitCode, string reason, CancellationToken _)
    {
        execution.Fail(exitCode, reason, Now());
        var disabled = task.RecordFailure(Now());
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await PublishStatusAsync(execution, CancellationToken.None);
        await notifications.PublishAsync(task.TeamId, ScheduledTaskNotifications.Failed(task, owner, disabled),
            CancellationToken.None);
        if (disabled)
            await auditWriter.RecordAsync(task.TeamId, null, ScheduledTaskAuditActions.AutoDisabled,
                new AuditTarget("scheduled-task", task.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                {
                    ["consecutiveFailures"] = task.ConsecutiveFailures,
                    ["failingSince"] = task.FailingSince
                }, CancellationToken.None);
        return ScheduledJobOutcome.Failed(reason, execution.Id.Value);
    }

    private sealed record ContainerResolution(string? Container, string? SkipReason, string? Error);

    /// <summary>
    ///     Running containers of the resource only; a configured name must be one of them (exact container name or
    ///     compose service name), so a task can never exec into another resource's container.
    /// </summary>
    private async Task<ContainerResolution> ResolveContainerAsync(ScheduledTask task, ScheduledTaskOwner owner,
        Server server, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Name, string Service)> candidates;
        if (owner.Kind == ScheduledTaskOwnerKind.Database)
        {
            var databaseId = new DatabaseResourceId(owner.Id);
            DatabaseResource database = dbContext.DatabaseResources.Single(item => item.Id == databaseId);
            if (database.LifecycleState != DatabaseLifecycleState.Running ||
                string.IsNullOrWhiteSpace(database.ContainerName))
                return new ContainerResolution(null, "Skipped: the database is not running.", null);
            candidates = await ListContainersAsync(server, $"name=^{database.ContainerName}$", cancellationToken);
        }
        else
        {
            var filter = owner.Kind == ScheduledTaskOwnerKind.Application
                ? $"label=vessel.application={owner.Id:D}"
                : $"label=com.docker.compose.project={$"vessel-svc-{owner.Id:N}"[..39]}";
            candidates = await ListContainersAsync(server, filter, cancellationToken);
        }

        if (candidates.Count == 0)
            return new ContainerResolution(null, "Skipped: the resource has no running container.", null);
        if (task.ContainerName is { } requested)
        {
            (string Name, string Service) match = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, requested, StringComparison.Ordinal) ||
                string.Equals(candidate.Service, requested, StringComparison.Ordinal));
            return match.Name is null
                ? new ContainerResolution(null, null,
                    $"No running container named '{requested}' belongs to this resource. Is the container name correct?")
                : new ContainerResolution(match.Name, null, null);
        }

        // Several replicas of one service (for example during a rolling update) run the newest; distinct services
        // need an explicit container name (upstream: "more than one container exists but no container name").
        if (candidates.Select(candidate => candidate.Service).Distinct(StringComparer.Ordinal).Count() > 1)
            return new ContainerResolution(null, null,
                "More than one container is running for this resource; set the task's container name.");
        return new ContainerResolution(candidates[0].Name, null, null);
    }

    private async Task<IReadOnlyList<(string Name, string Service)>> ListContainersAsync(Server server,
        string filter, CancellationToken cancellationToken)
    {
        ServerCommandResult result = await commands.RunAsync(ServerExecutionTarget.From(server),
            [RuntimeCli(server), "ps", "--filter", filter, "--filter", "status=running", "--format",
                "{{.Names}}\t{{.Label \"com.docker.compose.service\"}}"],
            ContainerLookupTimeout, null, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException("listing containers failed: " + result.StandardError.Trim());
        // `ps` lists the newest container first.
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var tab = line.IndexOf('\t', StringComparison.Ordinal);
                return tab < 0 ? (line, string.Empty) : (line[..tab].Trim(), line[(tab + 1)..].Trim());
            })
            .Where(candidate => candidate.Item1.Length > 0 && IsContainerName(candidate.Item1))
            .ToArray();
    }

    /// <summary>Secret values of the resource's environment (and database credentials), always redacted from output.</summary>
    private async Task<IReadOnlyList<string>> CollectSecretsAsync(ScheduledTask task, ScheduledTaskOwner owner,
        CancellationToken cancellationToken)
    {
        var secrets = new List<string>();
        try
        {
            if (owner.Kind == ScheduledTaskOwnerKind.Database)
            {
                var databaseId = new DatabaseResourceId(owner.Id);
                DatabaseResource database = dbContext.DatabaseResources.Single(item => item.Id == databaseId);
                secrets.Add(await secretVault.RevealForDeploymentAsync(task.TeamId, database.CredentialsReferenceId,
                    cancellationToken));
            }

            EnvironmentEntity? environment = dbContext.Environments.SingleOrDefault(item =>
                item.Id == owner.EnvironmentId);
            var applicationId = owner.Kind == ScheduledTaskOwnerKind.Application
                ? new ApplicationId(owner.Id)
                : (ApplicationId?)null;
            EnvironmentVariable[] variables = dbContext.EnvironmentVariables
                .Where(variable => variable.TeamId == task.TeamId &&
                                   variable.ValueKind == EnvironmentVariableValueKind.Secret &&
                                   variable.SecretReferenceId != null)
                .AsEnumerable()
                .Where(variable => variable.TargetType switch
                {
                    EnvironmentVariableTargetType.Team => true,
                    EnvironmentVariableTargetType.Project => environment is not null &&
                                                             variable.ProjectId == environment.ProjectId,
                    EnvironmentVariableTargetType.Environment => variable.EnvironmentId == owner.EnvironmentId,
                    EnvironmentVariableTargetType.Server => variable.ServerId == owner.ServerId,
                    EnvironmentVariableTargetType.Application => applicationId is not null &&
                                                                 variable.ApplicationId == applicationId,
                    EnvironmentVariableTargetType.Database => owner.Kind == ScheduledTaskOwnerKind.Database &&
                                                              variable.DatabaseResourceId ==
                                                              new DatabaseResourceId(owner.Id),
                    _ => false
                })
                .ToArray();
            foreach (EnvironmentVariable variable in variables)
                secrets.Add(await secretVault.RevealForDeploymentAsync(task.TeamId, variable.SecretReferenceId!.Value,
                    cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Pattern-based redaction still applies; a vault problem must not block the task.
        }

        return secrets.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private Task PublishOutputAsync(ScheduledTaskExecution execution, string redacted, ProcessStreamKind stream,
        CancellationToken cancellationToken)
    {
        return SafePublishAsync(execution, RealtimeOutputEvent, new
        {
            executionId = execution.Id.Value,
            taskId = execution.ScheduledTaskId.Value,
            stream = stream == ProcessStreamKind.StandardError ? "stderr" : "stdout",
            data = redacted
        }, cancellationToken);
    }

    private Task PublishStatusAsync(ScheduledTaskExecution execution, CancellationToken cancellationToken)
    {
        return SafePublishAsync(execution, RealtimeStatusEvent, new
        {
            executionId = execution.Id.Value,
            taskId = execution.ScheduledTaskId.Value,
            status = execution.Status.ToString(),
            finishedAt = execution.FinishedAt
        }, cancellationToken);
    }

    /// <summary>Realtime is best effort: output goes to the owning team's group only.</summary>
    private async Task SafePublishAsync(ScheduledTaskExecution execution, string eventName, object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await realtime.PublishAsync(new RealtimeGroup(RealtimeGroupKind.Team, execution.TeamId.Value.ToString("D")),
                new RealtimeMessage(eventName, payload), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The execution record is the source of truth.
        }
    }

    private static bool IsContainerName(string name)
    {
        return char.IsAsciiLetterOrDigit(name[0]) &&
               name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    }

    private static string RuntimeCli(Server server)
    {
        return server.Runtime == ContainerRuntimeKind.Podman ? "podman" : "docker";
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }
}

/// <summary>Scheduled task notifications. Messages carry names only, never the command or its output.</summary>
public static class ScheduledTaskNotifications
{
    public static RaiseNotificationRequest Failed(ScheduledTask task, ScheduledTaskOwner? owner, bool disabled)
    {
        var resource = owner is null ? string.Empty : $" on {owner.Kind.ToString().ToLowerInvariant()} '{owner.Name}'";
        var message = $"Scheduled task '{task.Name}'{resource} failed. Review the task's executions in Vessel.";
        if (disabled) message += " " + task.DisabledReason;
        return new RaiseNotificationRequest(NotificationEventTypes.ScheduledTaskFailed, NotificationSeverity.Error,
            NotificationTargetType.ScheduledTask, task.Id.Value.ToString("D"), $"Scheduled task failed: {task.Name}",
            message, Payload(task, disabled), null, null);
    }

    public static RaiseNotificationRequest Succeeded(ScheduledTask task, ScheduledTaskOwner owner)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.ScheduledTaskSucceeded, NotificationSeverity.Info,
            NotificationTargetType.ScheduledTask, task.Id.Value.ToString("D"),
            $"Scheduled task succeeded: {task.Name}",
            $"Scheduled task '{task.Name}' on {owner.Kind.ToString().ToLowerInvariant()} '{owner.Name}' succeeded.",
            Payload(task, false), null, null);
    }

    public static RaiseNotificationRequest Recovered(ScheduledTask task, ScheduledTaskOwner owner)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.ScheduledTaskRecovered, NotificationSeverity.Info,
            NotificationTargetType.ScheduledTask, task.Id.Value.ToString("D"),
            $"Scheduled task works again: {task.Name}",
            $"Scheduled task '{task.Name}' on {owner.Kind.ToString().ToLowerInvariant()} '{owner.Name}' works again.",
            Payload(task, false), null, null);
    }

    private static string Payload(ScheduledTask task, bool disabled)
    {
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["taskId"] = task.Id.Value,
            ["ownerKind"] = task.OwnerKind.ToString(),
            ["ownerId"] = task.OwnerId,
            ["consecutiveFailures"] = task.ConsecutiveFailures,
            ["autoDisabled"] = disabled
        });
    }
}
