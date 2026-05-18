using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vessel.Web.Hubs;

namespace Vessel.Web.Extensions;

public static class EndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapVesselEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Ok(new
        {
            service = "Vessel",
            status = "running"
        }));

        endpoints.MapControllers();
        endpoints.MapHub<VesselRealtimeHub>("/hubs/realtime");

        endpoints.MapHealthChecks("/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live", StringComparer.OrdinalIgnoreCase),
            ResponseWriter = WriteHealthResponseAsync
        });

        endpoints.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready", StringComparer.OrdinalIgnoreCase),
            ResponseWriter = WriteHealthResponseAsync
        });

        endpoints.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponseAsync
        });

        return endpoints;
    }

    private static async Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var checks = report.Entries.ToDictionary(
            entry => entry.Key,
            entry => (object?)new
            {
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = entry.Value.Duration.TotalMilliseconds,
                data = entry.Value.Data
            },
            StringComparer.Ordinal);

        var payload = new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks
        };

        await JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonSerializerOptions,
            context.RequestAborted);
    }
}
