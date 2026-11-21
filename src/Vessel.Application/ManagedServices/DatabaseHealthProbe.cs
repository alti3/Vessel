using System.Text.Json;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Domain.Databases;
using Vessel.Domain.Servers;

namespace Vessel.Application.ManagedServices;

/// <summary>
///     Reads a managed database container's state and Docker/Podman health-check status through
///     <see cref="IContainerRuntimeClient" /> and maps it to <see cref="DatabaseHealthState" />. Health is observed,
///     never assumed.
/// </summary>
public sealed class DatabaseHealthProbe(IContainerRuntimeClient runtime)
{
    public async Task<DatabaseHealthState> ProbeAsync(DatabaseResource database, Server server,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database.ContainerName)) return DatabaseHealthState.Unknown;
        string inspect;
        try
        {
            inspect = await runtime.InspectContainerAsync(ManagedRuntimeTargets.For(server), database.ContainerName,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The container is missing or the runtime cannot inspect it: the database is not serving.
            return DatabaseHealthState.Unhealthy;
        }

        return Map(inspect, database.HealthCheckEnabled);
    }

    /// <summary>
    ///     Maps <c>docker inspect</c> / <c>podman inspect</c> output: a container that is not running is Unhealthy; a
    ///     running container reports its health-check status (<c>starting</c>, <c>healthy</c>, <c>unhealthy</c>), and
    ///     without a health check its health is Unknown.
    /// </summary>
    public static DatabaseHealthState Map(string inspectJson, bool healthCheckEnabled)
    {
        try
        {
            using var document = JsonDocument.Parse(inspectJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return DatabaseHealthState.Unhealthy;
                root = root[0];
            }

            if (!root.TryGetProperty("State", out JsonElement state) || state.ValueKind != JsonValueKind.Object)
                return DatabaseHealthState.Unknown;
            var running = state.TryGetProperty("Running", out JsonElement runningElement) &&
                          runningElement.ValueKind == JsonValueKind.True;
            if (!running) return DatabaseHealthState.Unhealthy;
            if (!healthCheckEnabled) return DatabaseHealthState.Unknown;

            // Docker uses State.Health; older Podman versions use State.Healthcheck.
            JsonElement health = default;
            var hasHealth = (state.TryGetProperty("Health", out health) ||
                             state.TryGetProperty("Healthcheck", out health)) &&
                            health.ValueKind == JsonValueKind.Object;
            if (!hasHealth || !health.TryGetProperty("Status", out JsonElement status))
                return DatabaseHealthState.Unknown;
            return status.GetString()?.ToLowerInvariant() switch
            {
                "healthy" => DatabaseHealthState.Healthy,
                "unhealthy" => DatabaseHealthState.Unhealthy,
                "starting" => DatabaseHealthState.Starting,
                _ => DatabaseHealthState.Unknown
            };
        }
        catch (JsonException)
        {
            return DatabaseHealthState.Unknown;
        }
    }
}

/// <summary>
///     Polls the health of every running managed database (recurring Hangfire job) and records state changes.
/// </summary>
public sealed class DatabaseHealthPollingJob(
    IVesselDbContext dbContext,
    DatabaseHealthProbe probe,
    TimeProvider timeProvider)
{
    public const string RecurringJobId = "databases:health-poll";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        DatabaseResource[] running = dbContext.DatabaseResources
            .Where(database => database.LifecycleState == DatabaseLifecycleState.Running)
            .ToArray();
        foreach (DatabaseResource database in running)
        {
            Server? server = dbContext.Servers.SingleOrDefault(item => item.Id == database.ServerId);
            if (server is null || server.ConnectionType == ServerConnectionType.Ssh) continue;
            // ProbeAsync maps runtime errors to Unhealthy; only cancellation escapes.
            DatabaseHealthState observed = await probe.ProbeAsync(database, server, cancellationToken);
            database.RecordHealthProbe(observed, timeProvider.GetUtcNow());
        }

        if (running.Length > 0) await dbContext.SaveChangesAsync(cancellationToken);
    }
}

public static class DatabaseRecurringJobs
{
    public static void Register(IRecurringJobScheduler scheduler)
    {
        scheduler.AddOrUpdate<DatabaseHealthPollingJob>(DatabaseHealthPollingJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "* * * * *");
    }
}
