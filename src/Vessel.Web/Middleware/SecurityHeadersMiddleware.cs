using Microsoft.Extensions.Options;
using Vessel.Web.Configuration;

namespace Vessel.Web.Middleware;

public sealed class SecurityHeadersMiddleware(
    RequestDelegate next,
    IOptionsMonitor<SecurityHeadersOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        SecurityHeadersOptions securityHeadersOptions = options.CurrentValue;

        if (securityHeadersOptions.Enabled)
        {
            IHeaderDictionary headers = context.Response.Headers;

            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Referrer-Policy", "no-referrer");
            headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
            headers.TryAdd("Content-Security-Policy", securityHeadersOptions.ContentSecurityPolicy);
        }

        await next(context);
    }
}
