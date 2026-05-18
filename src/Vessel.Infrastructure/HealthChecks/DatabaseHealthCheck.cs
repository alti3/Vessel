using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.HealthChecks;

public sealed class DatabaseHealthCheck(IOptionsMonitor<DatabaseOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        DatabaseOptions databaseOptions = options.CurrentValue;

        if (!databaseOptions.Enabled) return HealthCheckResult.Healthy("PostgreSQL readiness check is disabled.");

        if (string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
            return HealthCheckResult.Unhealthy("PostgreSQL connection string is not configured.");

        try
        {
            await using NpgsqlConnection connection = new(databaseOptions.ConnectionString);
            using CancellationTokenSource timeout = CreateTimeout(databaseOptions.TimeoutSeconds, cancellationToken);

            await connection.OpenAsync(timeout.Token);

            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "select 1";
            command.CommandTimeout = databaseOptions.TimeoutSeconds;
            await command.ExecuteScalarAsync(timeout.Token);

            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception exception) when
            (exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
        }
    }

    internal static CancellationTokenSource CreateTimeout(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return timeout;
    }
}
