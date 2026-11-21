using System.Diagnostics;
using Serilog;
using Vessel.Application.Backups;
using Vessel.Application.Deployments;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Jobs;
using Vessel.Application.Jobs.Scheduling;
using Vessel.Application.ManagedServices;
using Vessel.Application.Monitoring;
using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Application.Notifications;
using Vessel.Application.Proxy;
using Vessel.Application.Servers.DockerCleanup;
using Vessel.Web.Configuration;
using Vessel.Web.Middleware;

namespace Vessel.Web.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication UseVesselWebHost(this WebApplication app)
    {
        // SEC-07: rewrite the client address from X-Forwarded-For only for explicitly trusted proxies, before any
        // rate limiter, throttle or audit reads it.
        if (app.Services.GetRequiredService<ForwardedHeadersSettings>() is { Enabled: true }) app.UseForwardedHeaders();

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ApiExceptionHandlingMiddleware>();

        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("CorrelationId", CorrelationIdMiddleware.GetCorrelationId(httpContext));
                diagnosticContext.Set("TraceId", Activity.Current?.TraceId.ToString());
            };
        });

        if (!app.Environment.IsDevelopment()) app.UseHsts();

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.UseRateLimiter();
        app.UseAuthentication();
        // SEC-13: after authentication (token known), around authorization and endpoints (their status is seen).
        app.UseMiddleware<ApiFailedRequestThrottleMiddleware>();
        app.UseAuthorization();

        return app;
    }

    public static WebApplication ScheduleVesselRecurringJobs(this WebApplication app)
    {
        IRecurringJobScheduler scheduler = app.Services.GetRequiredService<IRecurringJobScheduler>();
        try
        {
            CertificateRecurringJobs.Register(scheduler);
            DeploymentLogRecurringJobs.Register(scheduler);
            DeploymentQueueRecurringJobs.Register(scheduler);
            ServerHealthRecurringJobs.Register(scheduler);
            ServerMetricsRecurringJobs.Register(scheduler);
            DockerCleanupScheduleSyncJob.Register(scheduler);
            DatabaseRecurringJobs.Register(scheduler);
            BackupRecurringJobs.Register(scheduler);
            NotificationRecurringJobs.Register(scheduler);
            SchedulingRecurringJobs.Register(scheduler);
            Vessel.Application.Auditing.AuditRecurringJobs.Register(scheduler);
            Vessel.Application.Auth.AuthRecurringJobs.Register(scheduler);
            // Deployments that were running when the previous control-plane process stopped have no worker any
            // more: fail them and restart the queues.
            DeploymentQueueRecurringJobs.EnqueueStartupRecovery(
                app.Services.GetRequiredService<IBackgroundJobDispatcher>(),
                app.Services.GetRequiredService<TimeProvider>().GetUtcNow());
        }
        catch (BackgroundJobsUnavailableException exception)
        {
            Log.Warning(
                exception,
                "Recurring job registration skipped because Hangfire storage is disabled. RecurringJobId: {RecurringJobId}. MethodCall: {MethodCall}. CronExpression: {CronExpression}.",
                exception.RecurringJobId,
                exception.MethodCall,
                exception.CronExpression);
        }

        return app;
    }
}
