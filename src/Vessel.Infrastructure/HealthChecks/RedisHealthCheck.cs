using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.HealthChecks;

public sealed class RedisHealthCheck(IOptionsMonitor<RedisOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        RedisOptions redisOptions = options.CurrentValue;

        if (!redisOptions.Enabled)
        {
            return HealthCheckResult.Healthy("Redis readiness check is disabled.");
        }

        if (string.IsNullOrWhiteSpace(redisOptions.ConnectionString))
        {
            return HealthCheckResult.Unhealthy("Redis connection string is not configured.");
        }

        try
        {
            ConfigurationOptions configuration = ConfigurationOptions.Parse(redisOptions.ConnectionString);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectTimeout = (int)TimeSpan.FromSeconds(redisOptions.TimeoutSeconds).TotalMilliseconds;

            using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(configuration);
            using CancellationTokenSource timeout = DatabaseHealthCheck.CreateTimeout(
                redisOptions.TimeoutSeconds,
                cancellationToken);

            TimeSpan latency = await connection.GetDatabase().PingAsync().WaitAsync(timeout.Token);

            Dictionary<string, object> data = new(StringComparer.Ordinal)
            {
                ["latencyMs"] = latency.TotalMilliseconds
            };

            return HealthCheckResult.Healthy("Redis is reachable.", data);
        }
        catch (Exception exception) when (exception is RedisConnectionException or RedisTimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.");
        }
    }
}
