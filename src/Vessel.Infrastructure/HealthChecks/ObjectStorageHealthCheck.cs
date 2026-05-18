using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.HealthChecks;

public sealed class ObjectStorageHealthCheck(
    IOptionsMonitor<ObjectStorageOptions> options,
    IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public const string HttpClientName = "Vessel.ObjectStorageHealth";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectStorageOptions objectStorageOptions = options.CurrentValue;

        if (!objectStorageOptions.Enabled)
            return HealthCheckResult.Healthy("Object storage readiness check is disabled.");

        if (!Uri.TryCreate(objectStorageOptions.Endpoint, UriKind.Absolute, out Uri? endpoint))
            return HealthCheckResult.Unhealthy("Object storage endpoint is not configured.");

        try
        {
            HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
            httpClient.Timeout = TimeSpan.FromSeconds(objectStorageOptions.TimeoutSeconds);

            using HttpRequestMessage request = new(HttpMethod.Head, endpoint);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            return (int)response.StatusCode >= 500
                ? HealthCheckResult.Unhealthy("Object storage endpoint returned a server error.")
                : HealthCheckResult.Healthy("Object storage endpoint is reachable.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Object storage endpoint is not reachable.");
        }
    }
}
