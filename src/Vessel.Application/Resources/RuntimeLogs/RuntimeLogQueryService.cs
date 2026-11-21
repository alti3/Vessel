using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments;
using Vessel.Application.Docker;
using Vessel.Application.Monitoring;
using Vessel.Application.Persistence;
using Vessel.Application.Processes;
using Vessel.Application.Resources.Status;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Monitoring;

namespace Vessel.Application.Resources.RuntimeLogs;

/// <summary>
///     A runtime log read: last <c>Lines</c> per container (null reads everything, still capped at
///     <see cref="RuntimeLogQueryService.MaxLines" />), optionally only since a time, with or without timestamps.
/// </summary>
public sealed record RuntimeLogQuery(int? Lines = RuntimeLogQueryService.DefaultLines, DateTimeOffset? Since = null,
    bool Timestamps = true);

public sealed record RuntimeLogContainerDto(string Id, string Name, string State);

public sealed record RuntimeLogLineDto(DateTimeOffset? Timestamp, string Container, string Stream, string Message);

public sealed record RuntimeLogResultDto(
    string Kind,
    Guid ResourceId,
    IReadOnlyList<RuntimeLogContainerDto> Containers,
    IReadOnlyList<RuntimeLogLineDto> Lines,
    bool Truncated);

/// <summary>
///     Runtime (container) logs of applications, databases and services (SRV-05, upstream <c>RuntimeLogTargets</c>):
///     every container of the resource on its server (replicas, compose services, stopped and leftover rollout
///     containers) becomes one stream merged by timestamp. Every line passes through the resource's secret redaction
///     (its secret values, credentials and the redactor's patterns) before it leaves the service, for reads, live
///     follow and exports alike. Requires <see cref="VesselPermissions.ResourcesReadLogs" /> (read-sensitive) and an
///     instance administrator for host-local servers. Preview containers are not part of the production stream.
/// </summary>
public sealed class RuntimeLogQueryService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IContainerRuntimeClient runtime,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public const int DefaultLines = 100;
    public const int MaxLines = 10_000;
    public const int MaxLineLength = 16 * 1024;
    public static readonly TimeSpan FollowReauthorizationInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxFollowDuration = TimeSpan.FromHours(2);

    public async Task<RuntimeLogResultDto> ReadAsync(UserId actorUserId, TeamId teamId, ResourceRuntimeKind kind,
        Guid resourceId, RuntimeLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        AuthorizedRuntimeResource resource = Authorize(actorUserId, teamId, kind, resourceId);
        var tail = NormalizeTail(query.Lines);
        ContainerRuntimeTarget target = ContainerRuntimeTarget.For(resource.Server);
        IReadOnlyList<ContainerStateReading> containers = await ContainersAsync(resource, target, cancellationToken);
        RedactionContext context = await RedactionContextAsync(resource, cancellationToken);

        var lines = new List<RuntimeLogLineDto>();
        var truncated = false;
        foreach (ContainerStateReading container in containers)
        {
            var logRedactor = new DeploymentLogRedactor(redactor, context);
            var buffer = new Queue<RuntimeLogLineDto>();
            await foreach (ProcessOutputLine line in runtime.StreamContainerLogsAsync(target, container.Id,
                               new ContainerLogOptions(tail, query.Since, false), cancellationToken))
            {
                buffer.Enqueue(ToLine(line, container.Name, logRedactor, query.Timestamps));
                if (buffer.Count <= MaxLines) continue;
                buffer.Dequeue();
                truncated = true;
            }

            lines.AddRange(buffer);
        }

        RuntimeLogLineDto[] merged = Merge(lines);
        if (merged.Length > MaxLines)
        {
            merged = merged[^MaxLines..];
            truncated = true;
        }

        return new RuntimeLogResultDto(kind.ToString(), resourceId,
            containers.Select(container => new RuntimeLogContainerDto(container.Id, container.Name, container.State))
                .ToArray(), merged, truncated);
    }

    /// <summary>A redacted plain-text export (one line per log line, container name first).</summary>
    public async Task<string> ExportAsync(UserId actorUserId, TeamId teamId, ResourceRuntimeKind kind,
        Guid resourceId, RuntimeLogQuery query, CancellationToken cancellationToken = default)
    {
        RuntimeLogResultDto result = await ReadAsync(actorUserId, teamId, kind, resourceId, query, cancellationToken);
        var builder = new StringBuilder();
        foreach (RuntimeLogLineDto line in result.Lines)
        {
            if (line.Timestamp is { } timestamp)
                builder.Append(timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture)).Append(' ');
            builder.Append('[').Append(line.Container).Append("] ").Append(line.Message).Append('\n');
        }

        await auditWriter.RecordAsync(teamId, actorUserId, MonitoringAuditActions.RuntimeLogsExported,
            new AuditTarget(kind.ToString().ToLowerInvariant(), resourceId.ToString("D")), null,
            new Dictionary<string, object?> { ["lines"] = result.Lines.Count }, cancellationToken);
        return builder.ToString();
    }

    /// <summary>
    ///     Live follow of every container of the resource, merged as lines arrive. Access is re-checked every
    ///     <see cref="FollowReauthorizationInterval" />; the stream ends when access is lost or after
    ///     <see cref="MaxFollowDuration" />.
    /// </summary>
    public async IAsyncEnumerable<RuntimeLogLineDto> FollowAsync(UserId actorUserId, TeamId teamId,
        ResourceRuntimeKind kind, Guid resourceId, int? tail, bool timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AuthorizedRuntimeResource resource = Authorize(actorUserId, teamId, kind, resourceId);
        ContainerRuntimeTarget target = ContainerRuntimeTarget.For(resource.Server);
        IReadOnlyList<ContainerStateReading> containers = await ContainersAsync(resource, target, cancellationToken);
        RedactionContext context = await RedactionContextAsync(resource, cancellationToken);
        var initialTail = Math.Min(NormalizeTail(tail) ?? DefaultLines, 1000);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(MaxFollowDuration);
        var channel = Channel.CreateBounded<RuntimeLogLineDto>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        Task[] readers = containers.Select(container => Task.Run(async () =>
        {
            var logRedactor = new DeploymentLogRedactor(redactor, context);
            try
            {
                await foreach (ProcessOutputLine line in runtime.StreamContainerLogsAsync(target, container.Id,
                                   new ContainerLogOptions(initialTail, null, true), lifetime.Token))
                    await channel.Writer.WriteAsync(ToLine(line, container.Name, logRedactor, timestamps),
                        lifetime.Token);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One container's stream ending (stopped container, lost connection) does not end the others.
            }
        }, CancellationToken.None)).ToArray();
        _ = Task.WhenAll(readers).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        DateTimeOffset lastCheck = timeProvider.GetUtcNow();
        try
        {
            await foreach (RuntimeLogLineDto line in channel.Reader.ReadAllAsync(lifetime.Token)
                               .WithCancellation(lifetime.Token).ConfigureAwait(false))
            {
                if (timeProvider.GetUtcNow() - lastCheck >= FollowReauthorizationInterval)
                {
                    if (!IsStillAuthorized(actorUserId, teamId, kind, resourceId)) yield break;
                    lastCheck = timeProvider.GetUtcNow();
                }

                yield return line;
            }
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }

    /// <summary>Merges container streams by timestamp; lines without a timestamp keep their place after the previous one.</summary>
    public static RuntimeLogLineDto[] Merge(IEnumerable<RuntimeLogLineDto> lines)
    {
        return lines.Select((line, index) => (line, index))
            .OrderBy(item => item.line.Timestamp ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.index)
            .Select(item => item.line)
            .ToArray();
    }

    /// <summary>Splits the runtime's RFC 3339 timestamp prefix (<c>--timestamps</c>) from the message.</summary>
    public static (DateTimeOffset? Timestamp, string Message) SplitTimestamp(string content)
    {
        var text = content ?? string.Empty;
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var token = space > 0 ? text[..space] : text;
        if (token.Length >= 20 && token[4] == '-' && token[10] == 'T' && ParseTimestamp(token) is { } timestamp)
            return (timestamp, space > 0 ? text[(space + 1)..] : string.Empty);
        return (null, text);
    }

    private static DateTimeOffset? ParseTimestamp(string value)
    {
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
            if (end - dot - 1 > 7) value = value[..(dot + 8)] + value[end..];
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static RuntimeLogLineDto ToLine(ProcessOutputLine line, string container, DeploymentLogRedactor logRedactor,
        bool timestamps)
    {
        (DateTimeOffset? timestamp, string message) = SplitTimestamp(line.Content);
        if (message.Length > MaxLineLength) message = message[..MaxLineLength];
        message = logRedactor.Redact(message);
        return new RuntimeLogLineDto(timestamps ? timestamp : null, container,
            line.Stream == ProcessStreamKind.StandardError ? "stderr" : "stdout", message);
    }

    private AuthorizedRuntimeResource Authorize(UserId actorUserId, TeamId teamId, ResourceRuntimeKind kind,
        Guid resourceId)
    {
        AuthorizedRuntimeResource resource = RuntimeResourceAccess.Demand(dbContext, authorization, actorUserId,
            teamId, kind, resourceId, VesselPermissions.ResourcesReadLogs);
        // Host-local logs come from the control-plane runtime (host-root-equivalent).
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, resource.Server);
        return resource;
    }

    private bool IsStillAuthorized(UserId actorUserId, TeamId teamId, ResourceRuntimeKind kind, Guid resourceId)
    {
        try
        {
            Authorize(actorUserId, teamId, kind, resourceId);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static int? NormalizeTail(int? lines)
    {
        if (lines is null or < 0) return null;
        return Math.Min(lines.Value, MaxLines);
    }

    private async Task<IReadOnlyList<ContainerStateReading>> ContainersAsync(AuthorizedRuntimeResource resource,
        ContainerRuntimeTarget target, CancellationToken cancellationToken)
    {
        var serviceProjects = resource.Kind == ResourceRuntimeKind.Service
            ? new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                [RuntimeResourceLabels.ServiceProjectName(resource.Id)] = resource.Id
            }
            : new Dictionary<string, Guid>(StringComparer.Ordinal);
        var key = new RuntimeResourceKey(resource.Kind, resource.Id);
        IReadOnlyList<ContainerStateReading> all = await runtime.ListContainerStatesAsync(target, cancellationToken);
        ContainerStateReading[] own = all.Where(container => RuntimeResourceLabels.Resolve(container.Labels,
                serviceProjects) == key)
            .OrderBy(container => container.State == "running" ? 0 : 1)
            .ThenBy(container => container.Name, StringComparer.Ordinal)
            .ToArray();
        if (own.Length == 0) throw new KeyNotFoundException("No container of this resource exists on its server.");
        return own;
    }

    private async Task<RedactionContext> RedactionContextAsync(AuthorizedRuntimeResource resource,
        CancellationToken cancellationToken)
    {
        var secrets = new List<string>();
        if (resource.Application is { } application)
        {
            var projectId = dbContext.Environments.Where(environment => environment.Id == application.EnvironmentId)
                .Select(environment => environment.ProjectId).SingleOrDefault();
            EnvironmentVariable[] variables = dbContext.EnvironmentVariables
                .Where(variable => variable.TeamId == resource.TeamId &&
                                   variable.ValueKind == EnvironmentVariableValueKind.Secret &&
                                   variable.SecretReferenceId != null)
                .AsEnumerable()
                .Where(variable => variable.TargetType switch
                {
                    EnvironmentVariableTargetType.Team => true,
                    EnvironmentVariableTargetType.Project => variable.ProjectId == projectId,
                    EnvironmentVariableTargetType.Environment => variable.EnvironmentId == application.EnvironmentId,
                    EnvironmentVariableTargetType.Server => variable.ServerId == application.ServerId,
                    EnvironmentVariableTargetType.Application => variable.ApplicationId == application.Id,
                    _ => false
                })
                .ToArray();
            foreach (EnvironmentVariable variable in variables)
                secrets.Add(await RevealAsync(resource.TeamId, variable.SecretReferenceId!.Value, cancellationToken));
        }
        else if (resource.Database is { } database)
        {
            secrets.Add(await RevealAsync(resource.TeamId, database.CredentialsReferenceId, cancellationToken));
        }
        else if (resource.Service is { } service)
        {
            secrets.AddRange(SecretLikeValues(service.ConfigurationJson));
        }

        return new RedactionContext(secrets.Where(value => !string.IsNullOrEmpty(value)).ToArray(), null, null,
            true);
    }

    private async Task<string> RevealAsync(TeamId teamId, SecretReferenceId reference,
        CancellationToken cancellationToken)
    {
        try
        {
            return await secretVault.RevealForDeploymentAsync(teamId, reference, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A secret that cannot be revealed cannot be in a log line Vessel rendered; patterns still apply.
            return string.Empty;
        }
    }

    /// <summary>Service template inputs whose name marks them as secret (password, secret, token, key).</summary>
    public static IEnumerable<string> SecretLikeValues(string configurationJson)
    {
        Dictionary<string, string>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(configurationJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        foreach (var (key, value) in values ?? [])
        {
            var name = key.ToLowerInvariant();
            if (name.Contains("password", StringComparison.Ordinal) || name.Contains("secret", StringComparison.Ordinal) ||
                name.Contains("token", StringComparison.Ordinal) || name.EndsWith("key", StringComparison.Ordinal))
                yield return value;
        }
    }
}
