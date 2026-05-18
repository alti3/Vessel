using System.Diagnostics;
using Serilog;
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
        app.UseRateLimiter();

        return app;
    }
}
