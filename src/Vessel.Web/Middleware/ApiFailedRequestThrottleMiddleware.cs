using System.Globalization;
using System.Text.Json;
using Vessel.Application.Security.ApiAbuse;
using Vessel.Domain;
using Vessel.Shared.Errors;
using Vessel.Web.Security;

namespace Vessel.Web.Middleware;

/// <summary>
///     SEC-13: counts failed <c>/api</c> requests (401/403, and 404 without a valid token) per token or per client
///     address and answers <c>429</c> with <c>Retry-After</c> once a client exceeds the configured limit. Runs after
///     authentication (so the token is known) and around authorization and the endpoint (so their status is seen).
///     Health endpoints are never throttled.
/// </summary>
public sealed class ApiFailedRequestThrottleMiddleware(RequestDelegate next)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context, ApiFailedRequestThrottle throttle)
    {
        if (!throttle.Enabled || !IsApiRequest(context.Request.Path))
        {
            await next(context);
            return;
        }

        ApiClient client = ResolveClient(context);
        TimeSpan? blocked = await throttle.GetBlockAsync(client, context.RequestAborted);
        if (blocked is { } remaining)
        {
            await WriteThrottledAsync(context, remaining);
            return;
        }

        int statusCode;
        try
        {
            await next(context);
            statusCode = context.Response.StatusCode;
        }
        catch (UnauthorizedAccessException)
        {
            // Application authorization failures surface as 403 through the exception middleware.
            await RecordAsync(context, throttle, client, StatusCodes.Status403Forbidden);
            throw;
        }

        await RecordAsync(context, throttle, client, statusCode);
    }

    private static async Task RecordAsync(HttpContext context, ApiFailedRequestThrottle throttle, ApiClient client,
        int statusCode)
    {
        if (!ApiFailedRequestThrottle.IsCountedFailure(statusCode, client.TokenId.HasValue)) return;

        var lastRequest = string.Create(CultureInfo.InvariantCulture,
            $"{context.Request.Method} {context.Request.Path} -> {statusCode}");
        await throttle.RecordFailureAsync(client, lastRequest, CancellationToken.None);
    }

    private static bool IsApiRequest(PathString path)
    {
        return path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
               && !path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase)
               && !path.StartsWithSegments("/api/v1/health", StringComparison.OrdinalIgnoreCase);
    }

    private static ApiClient ResolveClient(HttpContext context)
    {
        string? ip = context.Connection.RemoteIpAddress?.ToString();
        if (!context.User.IsBearerTokenPrincipal()) return new ApiClient(ip, null, null);

        var tokenClaim = context.User.FindFirst(VesselClaimTypes.TokenId)?.Value;
        var teamClaim = context.User.FindFirst(VesselClaimTypes.TeamId)?.Value;
        return Guid.TryParse(tokenClaim, out Guid tokenId) && Guid.TryParse(teamClaim, out Guid teamId)
            ? new ApiClient(ip, new PersonalAccessTokenId(tokenId), new TeamId(teamId))
            : new ApiClient(ip, null, null);
    }

    private static async Task WriteThrottledAsync(HttpContext context, TimeSpan remaining)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "application/json";
        var response = new ApiErrorResponse(new ApiError(
            "api_failed_request_rate_limit",
            $"Too many failed API requests. Retry after {seconds.ToString(CultureInfo.InvariantCulture)} seconds.",
            new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds }));
        await JsonSerializer.SerializeAsync(context.Response.Body, response, JsonSerializerOptions,
            context.RequestAborted);
    }
}
