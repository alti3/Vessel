using System.Diagnostics;
using Microsoft.Extensions.Primitives;
using Serilog.Context;

namespace Vessel.Web.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        Activity.Current?.SetTag("vessel.correlation_id", correlationId);

        using IDisposable _ = LogContext.PushProperty("CorrelationId", correlationId);
        await next(context);
    }

    public static string GetCorrelationId(HttpContext context)
    {
        return context.Items.TryGetValue(HeaderName, out var value) && value is string correlationId
            ? correlationId
            : context.TraceIdentifier;
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out StringValues headerValue))
        {
            var candidate = headerValue.ToString();

            if (candidate.Length is > 0 and <= 128) return candidate;
        }

        return context.TraceIdentifier;
    }
}
