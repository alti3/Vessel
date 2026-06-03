using System.Diagnostics;
using Serilog;
using Vessel.Application.Jobs;
using Vessel.Application.Proxy;
using Vessel.Web.Middleware;

namespace Vessel.Web.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication UseVesselWebHost(this WebApplication app)
    {
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
        app.UseAuthorization();

        return app;
    }

    public static WebApplication ScheduleVesselRecurringJobs(this WebApplication app)
    {
        IRecurringJobScheduler scheduler = app.Services.GetRequiredService<IRecurringJobScheduler>();
        try
        {
            CertificateRecurringJobs.Register(scheduler);
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
