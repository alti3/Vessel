using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vessel.Application.Notifications;
using Vessel.Application.Redis;
using Vessel.Domain;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Security.ApiAbuse;

/// <summary>Limits for <see cref="ApiFailedRequestThrottle" />. A limit of 0 disables the throttle.</summary>
public sealed record ApiFailedRequestThrottleSettings(int Limit, TimeSpan Window)
{
    public static ApiFailedRequestThrottleSettings Default { get; } = new(30, TimeSpan.FromSeconds(60));
}

/// <summary>The client a failed API request is counted for.</summary>
public sealed record ApiClient(string? IpAddress, PersonalAccessTokenId? TokenId, TeamId? TokenTeamId)
{
    /// <summary>Authenticated tokens are counted per token; everything else per source address.</summary>
    public string Key => TokenId is { } tokenId
        ? "token:" + tokenId.Value.ToString("N")
        : "ip:" + Hash(IpAddress ?? "unknown");

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();
    }
}

/// <summary>
///     SEC-13 (Coolify v4.7.0 <c>ThrottleFailedApiRequests</c> parity): API clients that keep failing — 401 and 403,
///     and 404 without a valid token — are limited to <see cref="ApiFailedRequestThrottleSettings.Limit" /> failed
///     requests per window per token (or per source address without a valid token). Beyond that every request of the
///     client gets 429 for 1 minute, then 5 and 15 minutes when it reaches the limit again within an hour. The token's
///     team receives at most one "API token failing" notification per day.
/// </summary>
public sealed class ApiFailedRequestThrottle(
    IRedisCache cache,
    TimeProvider timeProvider,
    INotificationEventPublisher notifications,
    ApiFailedRequestThrottleSettings settings,
    ILogger<ApiFailedRequestThrottle> logger)
{
    private const string Prefix = "vessel:api-failed-requests:";

    private static readonly Action<ILogger, Exception?> NotificationFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(NotificationFailed)),
        "Could not publish the API token failing notification.");
    private static readonly int[] BlockSeconds = [60, 300, 900];
    private static readonly TimeSpan StrikeMemory = TimeSpan.FromHours(1);

    public bool Enabled => settings.Limit > 0;

    public static bool IsCountedFailure(int statusCode, bool hasValidToken)
    {
        return statusCode is 401 or 403 || (statusCode == 404 && !hasValidToken);
    }

    /// <summary>Remaining block time for the client, or <c>null</c> when it is not blocked.</summary>
    public async Task<TimeSpan?> GetBlockAsync(ApiClient client, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;

        var stored = await cache.GetStringAsync(Prefix + client.Key + ":blocked-until", cancellationToken);
        if (!long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockedUntil))
            return null;

        var remaining = blockedUntil - timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return remaining > 0 ? TimeSpan.FromSeconds(remaining) : null;
    }

    /// <summary>Counts one failed request. Returns the block duration when this failure reached the limit.</summary>
    public async Task<TimeSpan?> RecordFailureAsync(
        ApiClient client,
        string lastRequest,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;

        var windowSeconds = Math.Max(1, (long)settings.Window.TotalSeconds);
        var window = timeProvider.GetUtcNow().ToUnixTimeSeconds() / windowSeconds;
        var counterKey = $"{Prefix}{client.Key}:window:{window.ToString(CultureInfo.InvariantCulture)}";
        var failures = await cache.IncrementAsync(counterKey, TimeSpan.FromSeconds(windowSeconds * 2),
            cancellationToken);
        if (failures < settings.Limit) return null;

        var strikes = await cache.IncrementAsync(Prefix + client.Key + ":strikes", StrikeMemory, cancellationToken);
        var seconds = BlockSeconds[(int)Math.Min(strikes, BlockSeconds.Length) - 1];
        var blockedUntil = timeProvider.GetUtcNow().ToUnixTimeSeconds() + seconds;
        await cache.SetStringAsync(Prefix + client.Key + ":blocked-until",
            blockedUntil.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(seconds), cancellationToken);

        await NotifyTeamAsync(client, lastRequest, cancellationToken);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task NotifyTeamAsync(ApiClient client, string lastRequest, CancellationToken cancellationToken)
    {
        if (client.TokenId is not { } tokenId || client.TokenTeamId is not { } teamId) return;

        try
        {
            var day = timeProvider.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var sent = await cache.IncrementAsync($"{Prefix}notified:{tokenId.Value:N}:{day}", TimeSpan.FromDays(2),
                cancellationToken);
            if (sent != 1) return;

            await notifications.PublishAsync(teamId,
                ApiTokenFailing(tokenId.Value, settings.Limit, lastRequest), cancellationToken);
        }
#pragma warning disable CA1031 // The throttle must never fail the request because a notification could not be sent.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            NotificationFailed(logger, exception);
        }
    }

    /// <summary>The "API token failing" notification. It names the token id and request line, never the secret.</summary>
    public static RaiseNotificationRequest ApiTokenFailing(Guid tokenId, int failedRequests, string lastRequest)
    {
        return new RaiseNotificationRequest(
            NotificationEventTypes.ApiTokenFailing,
            NotificationSeverity.Warning,
            NotificationTargetType.Team,
            null,
            "API token keeps failing",
            $"An API token of this team made {failedRequests.ToString(CultureInfo.InvariantCulture)} failed requests "
            + $"within a minute and is temporarily blocked. Last request: {lastRequest}.",
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["tokenId"] = tokenId,
                ["failedRequests"] = failedRequests
            }),
            "/tokens",
            null);
    }
}
