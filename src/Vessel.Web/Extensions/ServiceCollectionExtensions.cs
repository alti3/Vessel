using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Application.Resources.RuntimeLogs;
using Vessel.Application.Resources.Status;
using Vessel.Application.Servers.Alerts;
using Vessel.Application.Servers.DockerCleanup;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vessel.Application.Auditing;
using Vessel.Application.Auth;
using Vessel.Application.Authorization;
using Vessel.Application.Backups;
using Vessel.Application.Common;
using Vessel.Application.Dashboard;
using Vessel.Application.Deployments;
using Vessel.Application.Deployments.Previews;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Deployments.Rollback;
using Vessel.Application.Deployments.Settings;
using Vessel.Application.Diagnostics;
using Vessel.Application.Email;
using Vessel.Application.Instance;
using Vessel.Application.Jobs;
using Vessel.Application.ManagedServices;
using Vessel.Application.ManagedServices.Templates;
using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Proxy.Certificates;
using Vessel.Application.Proxy.Domains;
using Vessel.Application.Realtime;
using Vessel.Application.Redis;
using Vessel.Application.Resources;
using Vessel.Application.Security;
using Vessel.Application.Security.ApiAbuse;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Validation;
using Vessel.Application.Ssh;
using Vessel.Application.Ssh.Keys;
using Vessel.Application.Storage;
using Vessel.Application.Terminals;
using Vessel.Application.Webhooks;
using Vessel.Application.Sources;
using Vessel.Infrastructure.HealthChecks;
using Vessel.Shared.Configuration;
using Vessel.Web.Configuration;
using Vessel.Web.Diagnostics;
using Vessel.Web.Middleware;
using Vessel.Web.Realtime;
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
        services
            .AddRazorComponents()
            .AddInteractiveServerComponents();
        services.AddSignalR();
        services.AddHttpContextAccessor();
        services.AddCascadingAuthenticationState();

        services.AddVesselWebOptions(configuration);
        services.AddVesselApplicationServices(configuration);
        services.AddVesselAuthenticationAndAuthorization();
        services.AddVesselRateLimiting(configuration);
        services.AddVesselForwardedHeaders(configuration);
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
            .Validate(options => options.PasswordResetTokenMinutes > 0,
                "Password reset token duration must be positive.")
            .Validate(options => options.InvitationExpirationDays > 0, "Invitation expiration must be positive.")
            .ValidateOnStart();

        services
            .AddOptions<InstanceUrlOptions>()
            .Bind(configuration.GetSection(InstanceUrlOptions.SectionName))
            .Validate(options => string.IsNullOrWhiteSpace(options.PublicUrl)
                                 || InstanceUrlOptions.ParseBaseUrl(options.PublicUrl) is not null,
                "Vessel:Host:PublicUrl must be an absolute http(s) URL without credentials, query, or fragment.")
            .ValidateOnStart();

        services.TryAddScoped<IVesselDbContext, UnavailableVesselDbContext>();
        services.TryAddScoped<IUnitOfWork>(provider => provider.GetRequiredService<IVesselDbContext>());
        services.TryAddScoped<IAuditWriter, NoopAuditWriter>();
        services.AddScoped<VesselAuthenticationService>();
        services.AddScoped<VesselTokenService>();
        services.AddScoped<VesselTeamService>();
        services.AddScoped<VesselAuthorizationService>();
        // IAM-05 access grants: member layer + request token layer, consulted by VesselAuthorizationService.
        services.AddScoped<IApiTokenAccessContext, HttpApiTokenAccessContext>();
        services.AddScoped<IAccessResolver, AccessResolver>();
        services.AddScoped<AccessScopedCatalog>();
        services.AddScoped<InstanceSettingsService>();
        // SEC-07 / SEC-13 abuse throttles (Redis when enabled, otherwise in-process counters).
        services.TryAddSingleton<IRedisCache, InMemoryRedisCache>();
        services.AddSingleton<WebhookFailureThrottle>();
        services.AddScoped<ApiFailedRequestThrottle>();
        services.AddScoped<ResourceManagementService>();
        services.AddScoped<StartDeploymentService>();
        services.AddScoped<DeploymentQueryService>();
        services.AddScoped<DeploymentLogRetentionService>();
        services.AddScoped<IDeploymentRunner, DeploymentRunner>();
        services.AddSingleton<IDeploymentCancellationRegistry, DeploymentCancellationRegistry>();
        services.AddScoped<DeploymentQueueService>();
        services.AddScoped<DeploymentQueueJob>();
        services.AddScoped<PreviewLifecycleService>();
        services.AddScoped<CleanupPreviewJob>();
        services.AddScoped<RollbackService>();
        services.AddScoped<DeploymentSettingsService>();
        services.AddScoped<TerminalSessionManager>();
        services.TryAddSingleton<TerminalAttachTokens>();
        services.TryAddSingleton<SshHostKeyScanThrottle>();
        services.AddScoped<ServerSshConnectionService>();
        services.AddScoped<SshKeyService>();
        services.AddScoped<IGitCredentialProvider, GitCredentialResolver>();
        services.AddScoped<GitSourceService>();
        services.AddScoped<SshKeyRotationService>();
        services.AddScoped<RotateSshKeyJob>();
        services.AddScoped<ServerManagementService>();
        services.AddScoped<ServerValidationService>();
        services.AddScoped<ValidateServerJob>();
        services.AddScoped<ServerHealthPollingService>();
        services.AddScoped<ServerHealthPollingJob>();
        // SRV-03/04/05/07/08: metrics, runtime status, runtime logs, Docker cleanup and server alerts.
        services.AddScoped<ServerMetricsCollectionService>();
        services.AddScoped<ServerMetricsQueryService>();
        services.AddScoped<ServerMetricsRetentionService>();
        services.AddScoped<ServerMonitoringSettingsService>();
        services.TryAddScoped<IServerMetricsPruner, QueryableServerMetricsPruner>();
        services.AddScoped<ServerMetricsDispatchJob>();
        services.AddScoped<CollectServerMetricsJob>();
        services.AddScoped<ServerMetricsRetentionJob>();
        services.AddScoped<ServerAlertEvaluator>();
        services.AddScoped<ResourceStatusReconciler>();
        services.AddScoped<ResourceRuntimeStatusService>();
        services.AddScoped<NotifyResourceStoppedJob>();
        services.AddScoped<RuntimeLogQueryService>();
        services.AddScoped<DockerCleanupService>();
        services.AddScoped<RunDockerCleanupJob>();
        services.AddScoped<DockerCleanupScheduleSyncJob>();
        services.AddScoped<ManagedDatabaseService>();
        services.AddScoped<DatabaseRestoreService>();
        services.AddScoped<DatabaseHealthProbe>();
        services.AddScoped<DatabaseHealthPollingJob>();
        services.AddScoped<DatabaseBackupService>();
        services.AddScoped<BackupDestinationService>();
        services.AddScoped<BackupDestinationConnector>();
        services.AddScoped<BackupArtifactReader>();
        services.AddScoped<CheckMissingBackupsJob>();
        services.AddScoped<RevalidateBackupDestinationsJob>();
        services.AddSingleton(provider => ServiceTemplateCatalog.Create(
            provider.GetService<IServiceTemplateComposeProcessor>(), provider.GetService<IServiceTemplateSource>()));
        services.AddScoped<DomainRoutingService>();
        services.AddScoped<ProxyConfigurationService>();
        services.AddScoped<ProxyLifecycleService>();
        services.AddScoped<IProxyStatusProbe>(provider => provider.GetRequiredService<ProxyLifecycleService>());
        services.AddScoped<CertificateManagementService>();
        services.AddScoped<CertificateInventoryService>();
        services.AddScoped<CertificateSyncJob>();
        services.AddScoped<ProxyConfigurationHistoryCleanupJob>();
        services.AddScoped<ProxyRouteProjector>();
        services.AddScoped<ProxyConfigurationProtector>();
        services.AddScoped<RoutePolicyWriter>();
        services.AddScoped<ServiceDomainService>();
        services.AddScoped<ServiceRoutingCoordinator>();
        services.AddScoped<CertificateIssuanceJob>();
        services.AddScoped<WebhookReceiptService>();
        services.AddScoped<WebhookProcessingService>();
        services.AddScoped<ApplicationWebhookConfigurationService>();
        services.AddScoped<NotificationTargetService>();
        services.AddScoped<NotificationCenterService>();
        services.AddScoped<NotificationEventService>();
        services.AddScoped<NotificationDispatchService>();
        services.AddScoped<DispatchNotificationJob>();
        services.AddScoped<RaiseNotificationJob>();
        services.AddScoped<NotificationThrottlePurgeJob>();
        services.AddSingleton<InstanceLinkBuilder>();
        services.AddScoped<InstanceEmailSender>();
        services.AddScoped<InstanceEmailOutbox>();
        services.AddScoped<SendInstanceEmailJob>();
        services.AddScoped<InstanceEmailSettingsService>();
        services.AddScoped<AuthEmailService>();
        services.AddScoped<TotpService>();
        services.AddScoped<SecretReencryptionService>();
        services.AddScoped<SecretReencryptionJob>();
        services.TryAddScoped<ISecretVault, UnavailableSecretVault>();
        services.TryAddSingleton<IDatabaseBackupProvider, UnavailableDatabaseBackupProvider>();
        services.TryAddSingleton<IObjectStorage, UnavailableObjectStorage>();
        services.TryAddSingleton<IBackupStorageProvider, UnavailableBackupStorageProvider>();
        services.TryAddSingleton<IDistributedLockManager, InMemoryDistributedLockManager>();
        services.TryAddSingleton<IBackgroundJobDispatcher, UnavailableBackgroundJobDispatcher>();
        services.TryAddSingleton<IRecurringJobScheduler, UnavailableRecurringJobScheduler>();
        services.TryAddSingleton<ITerminalProcessBridge, UnavailableTerminalProcessBridge>();
        services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();
        services.AddScoped<IDashboardOverviewQuery, EmptyDashboardOverviewQuery>();
        services.AddScoped<IProjectCatalogQuery, EmptyProjectCatalogQuery>();
        services.AddScoped<IServerCatalogQuery, EmptyServerCatalogQuery>();
        services.AddScoped<IApplicationCatalogQuery, EmptyApplicationCatalogQuery>();
        services.AddScoped<IDeploymentCatalogQuery, EmptyDeploymentCatalogQuery>();
        services.AddScoped<IDatabaseCatalogQuery, EmptyDatabaseCatalogQuery>();
        services.AddScoped<INotificationCatalogQuery, EmptyNotificationCatalogQuery>();
        services.AddScoped<ISettingsCatalogQuery, EmptySettingsCatalogQuery>();
        services.TryAddScoped<IServerHealthQuery, EmptyServerHealthQuery>();

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
            foreach (var permission in VesselPermissions.All)
                options.AddPolicy(permission, policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission)));

            options.AddPolicy(VesselAuthorizationPolicies.InteractiveSession, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => VesselAuthorizationPolicies.IsInteractiveSession(context.User)));
            options.AddPolicy(VesselAuthorizationPolicies.InteractiveSessionOrRootToken, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    VesselAuthorizationPolicies.IsInteractiveSessionOrRootToken(context.User)));
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
                .AddSource(VesselDiagnostics.InstrumentationName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.EnrichWithHttpRequest = (activity, request) =>
                    {
                        if (request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName,
                                out StringValues correlationId))
                            activity.SetTag("vessel.correlation_id", correlationId.ToString());
                    };
                })
                .AddHttpClientInstrumentation(options =>
                    options.EnrichWithHttpRequestMessage = OutboundUrlTelemetryRedaction.EnrichWithHttpRequestMessage)
                // Must precede exporters: processors run in registration order.
                .AddProcessor(new OutboundUrlTelemetryRedaction());

            if (!string.IsNullOrWhiteSpace(diagnosticsOptions.OtlpEndpoint))
                tracing.AddOtlpExporter(options => { options.Endpoint = new Uri(diagnosticsOptions.OtlpEndpoint); });
        });

        openTelemetryBuilder.WithMetrics(metrics =>
        {
            metrics
                .AddMeter(VesselDiagnostics.InstrumentationName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (!string.IsNullOrWhiteSpace(diagnosticsOptions.OtlpEndpoint))
                metrics.AddOtlpExporter(options => { options.Endpoint = new Uri(diagnosticsOptions.OtlpEndpoint); });
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

    private static IServiceCollection AddVesselForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ForwardedHeadersSettings settings = configuration
            .GetSection(ForwardedHeadersSettings.SectionName)
            .Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        IReadOnlyList<string> failures = settings.Validate();
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(' ', failures));

        services.AddSingleton(settings);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = settings.ForwardLimit;
            // No blanket trust: drop the framework's loopback defaults and trust only what is configured.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in settings.KnownProxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
            foreach (var network in settings.KnownNetworks)
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        });

        return services;
    }

    private static IServiceCollection AddVesselRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        RateLimitOptions rateLimitOptions = configuration
            .GetSection(RateLimitOptions.SectionName)
            .Get<RateLimitOptions>() ?? new RateLimitOptions();

        services.AddSingleton(rateLimitOptions.ApiFailedRequests.PermitLimit <= 0
            ? new ApiFailedRequestThrottleSettings(0, TimeSpan.FromSeconds(60))
            : new ApiFailedRequestThrottleSettings(rateLimitOptions.ApiFailedRequests.PermitLimit,
                TimeSpan.FromSeconds(rateLimitOptions.ApiFailedRequests.WindowSeconds)));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // SEC-13: every /api route shares the "api" budget per client address, not only the endpoints that opt
            // in. Named policies (auth, terminal, ...) still apply on top.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                    ? CreateLimiter(context, rateLimitOptions.Api, "api-global")
                    : RateLimitPartition.GetNoLimiter("unlimited"));
            options.AddPolicy("auth", context => CreateLimiter(context, rateLimitOptions.Auth));
            options.AddPolicy("webhooks", context => CreateLimiter(context, rateLimitOptions.Webhooks));
            options.AddPolicy("api", context => CreateLimiter(context, rateLimitOptions.Api));
            options.AddPolicy("terminal", context => CreateLimiter(context, rateLimitOptions.Terminal));
        });

        return services;
    }

    private static RateLimitPartition<string> CreateLimiter(
        HttpContext context,
        RateLimitPolicyOptions options,
        string? bucket = null)
    {
        // RemoteIpAddress is the real client only when it was rewritten by ForwardedHeaders from a trusted proxy.
        var partitionKey = (bucket is null ? string.Empty : bucket + ":")
                           + (context.Connection.RemoteIpAddress?.ToString() ?? context.TraceIdentifier);

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
