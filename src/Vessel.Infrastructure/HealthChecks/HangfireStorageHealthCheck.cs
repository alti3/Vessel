using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.HealthChecks;

public sealed class HangfireStorageHealthCheck(IOptionsMonitor<HangfireStorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        HangfireStorageOptions hangfireOptions = options.CurrentValue;

        if (!hangfireOptions.Enabled) return HealthCheckResult.Healthy("Hangfire storage readiness check is disabled.");

        if (!string.Equals(hangfireOptions.StorageProvider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
            return HealthCheckResult.Unhealthy("Hangfire storage provider is unsupported.");

        if (string.IsNullOrWhiteSpace(hangfireOptions.ConnectionString))
            return HealthCheckResult.Unhealthy("Hangfire storage connection string is not configured.");

        try
        {
            await using NpgsqlConnection connection = new(hangfireOptions.ConnectionString);
            using CancellationTokenSource timeout = DatabaseHealthCheck.CreateTimeout(
                hangfireOptions.TimeoutSeconds,
                cancellationToken);

            await connection.OpenAsync(timeout.Token);

            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "select 1";
            command.CommandTimeout = hangfireOptions.TimeoutSeconds;
            await command.ExecuteScalarAsync(timeout.Token);

            return HealthCheckResult.Healthy("Hangfire PostgreSQL storage is reachable.");
        }
        catch (Exception exception) when
            (exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Hangfire PostgreSQL storage is not reachable.");
        }
    }
}
