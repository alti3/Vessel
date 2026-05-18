using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vessel.Infrastructure.HealthChecks;
using Vessel.Shared.Configuration;
using Vessel.Web.Configuration;
using Vessel.Web.Middleware;

namespace Vessel.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVesselWebHost(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        if (!VesselEnvironmentNames.IsKnown(environment.EnvironmentName))
            throw new InvalidOperationException(
                $"Unsupported environment '{environment.EnvironmentName}'. Supported values are Development, Staging, Production, and Testing.");

        services.AddControllers();
        services.AddHttpContextAccessor();

        services.AddVesselWebOptions(configuration);
        services.AddVesselRateLimiting(configuration);
        services.AddVesselOpenTelemetry(configuration, environment);
        services.AddVesselHealthChecks();

        return services;
    }

    private static IServiceCollection AddVesselWebOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<VesselHostOptions>()
            .Bind(configuration.GetSection(VesselHostOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<VesselHostOptions>, VesselHostOptionsValidator>());

        services
            .AddOptions<DiagnosticsOptions>()
            .Bind(configuration.GetSection(DiagnosticsOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<DiagnosticsOptions>, DiagnosticsOptionsValidator>());

        services
            .AddOptions<SecurityHeadersOptions>()
            .Bind(configuration.GetSection(SecurityHeadersOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<SecurityHeadersOptions>, SecurityHeadersOptionsValidator>());

        services
            .AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>());

        return services;
    }

    private static IServiceCollection AddVesselOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        DiagnosticsOptions diagnosticsOptions = configuration
            .GetSection(DiagnosticsOptions.SectionName)
            .Get<DiagnosticsOptions>() ?? new DiagnosticsOptions();

        if (!diagnosticsOptions.OpenTelemetryEnabled) return services;

        VesselHostOptions hostOptions = configuration
            .GetSection(VesselHostOptions.SectionName)
            .Get<VesselHostOptions>() ?? new VesselHostOptions();

        IOpenTelemetryBuilder openTelemetryBuilder = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(hostOptions.ServiceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = environment.EnvironmentName
                }));

        openTelemetryBuilder.WithTracing(tracing =>
        {
            tracing
                .AddSource("Vessel")
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.EnrichWithHttpRequest = (activity, request) =>
                    {
                        if (request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName,
                                out StringValues correlationId))
                            activity.SetTag("vessel.correlation_id", correlationId.ToString());
                    };
                })
                .AddHttpClientInstrumentation();

            if (!string.IsNullOrWhiteSpace(diagnosticsOptions.OtlpEndpoint))
                tracing.AddOtlpExporter(options => { options.Endpoint = new Uri(diagnosticsOptions.OtlpEndpoint); });
        });

        return services;
    }

    private static IServiceCollection AddVesselHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck(
                "self",
                () => HealthCheckResult.Healthy("Vessel host is running."),
                ["live", "ready"])
            .AddCheck<DatabaseHealthCheck>(
                "postgresql",
                tags: ["ready"])
            .AddCheck<RedisHealthCheck>(
                "redis",
                tags: ["ready"])
            .AddCheck<HangfireStorageHealthCheck>(
                "hangfire-storage",
                tags: ["ready"])
            .AddCheck<ObjectStorageHealthCheck>(
                "object-storage",
                tags: ["ready"]);

        return services;
    }

    private static IServiceCollection AddVesselRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        RateLimitOptions rateLimitOptions = configuration
            .GetSection(RateLimitOptions.SectionName)
            .Get<RateLimitOptions>() ?? new RateLimitOptions();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", context => CreateLimiter(context, rateLimitOptions.Auth));
            options.AddPolicy("webhooks", context => CreateLimiter(context, rateLimitOptions.Webhooks));
            options.AddPolicy("api", context => CreateLimiter(context, rateLimitOptions.Api));
            options.AddPolicy("terminal", context => CreateLimiter(context, rateLimitOptions.Terminal));
        });

        return services;
    }

    private static RateLimitPartition<string> CreateLimiter(
        HttpContext context,
        RateLimitPolicyOptions options)
    {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString()
                           ?? context.TraceIdentifier;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = options.PermitLimit,
                QueueLimit = 0,
                Window = TimeSpan.FromSeconds(options.WindowSeconds)
            });
    }
}
