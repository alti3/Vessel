using System.Diagnostics;
using System.Text.Json;
using Vessel.Shared.Errors;

namespace Vessel.Web.Middleware;

public sealed class ApiExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ApiExceptionHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, string, string, Exception?> UnhandledRequestFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1, nameof(UnhandledRequestFailure)),
            "Unhandled request failure. CorrelationId: {CorrelationId}; TraceId: {TraceId}");

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

            UnhandledRequestFailure(logger, correlationId, traceId, exception);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            ApiErrorResponse response = new(new ApiError(
                "unexpected_error",
                "An unexpected error occurred.",
                new Dictionary<string, object?>
                {
                    ["correlationId"] = correlationId,
                    ["traceId"] = traceId
                }));

            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                JsonSerializerOptions,
                context.RequestAborted);
        }
    }
}
