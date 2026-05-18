using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vessel.Application.Auth;
using Vessel.Application.Authorization;
using Vessel.Application.Security;
using Vessel.Infrastructure.HealthChecks;
using Vessel.Shared.Configuration;
using Vessel.Web.Configuration;
using Vessel.Web.Middleware;
using Vessel.Web.Security;

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
        services.AddVesselApplicationServices(configuration);
        services.AddVesselAuthenticationAndAuthorization();
        services.AddVesselRateLimiting(configuration);
        services.AddVesselOpenTelemetry(configuration, environment);
        services.AddVesselHealthChecks();

        return services;
    }

    private static IServiceCollection AddVesselApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(options => options.LockoutThreshold > 0, "Auth lockout threshold must be positive.")
            .Validate(options => options.LockoutMinutes > 0, "Auth lockout duration must be positive.")
            .Validate(options => options.PasswordResetTokenMinutes > 0, "Password reset token duration must be positive.")
            .Validate(options => options.InvitationExpirationDays > 0, "Invitation expiration must be positive.")
            .ValidateOnStart();

        services.AddScoped<VesselAuthenticationService>();
        services.AddScoped<VesselTokenService>();
        services.AddScoped<VesselTeamService>();
        services.AddScoped<VesselAuthorizationService>();
        services.AddScoped<TotpService>();

        return services;
    }

    private static IServiceCollection AddVesselAuthenticationAndAuthorization(this IServiceCollection services)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = VesselAuthenticationSchemes.Smart;
                options.DefaultChallengeScheme = VesselAuthenticationSchemes.Smart;
                options.DefaultSignInScheme = VesselAuthenticationSchemes.Cookie;
            })
            .AddPolicyScheme(
                VesselAuthenticationSchemes.Smart,
                "Vessel cookie or bearer authentication",
                options =>
                {
                    options.ForwardDefaultSelector = context =>
                    {
                        string? authorization = context.Request.Headers.Authorization;
                        return !string.IsNullOrWhiteSpace(authorization)
                               && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? VesselAuthenticationSchemes.Bearer
                            : VesselAuthenticationSchemes.Cookie;
                    };
                })
            .AddCookie(
                VesselAuthenticationSchemes.Cookie,
                options =>
                {
                    options.Cookie.Name = "__Host-Vessel";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.LoginPath = "/api/auth/login";
                    options.LogoutPath = "/api/auth/logout";
                })
            .AddScheme<AuthenticationSchemeOptions, VesselBearerAuthenticationHandler>(
                VesselAuthenticationSchemes.Bearer,
                _ => { });

        services.AddAuthorization(options =>
        {
            foreach (string permission in VesselPermissions.All)
            {
                options.AddPolicy(permission, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission)));
            }
        });
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

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
