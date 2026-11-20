using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Redis;
using Vessel.Domain.Webhooks;

namespace Vessel.Application.Webhooks;

/// <summary>
///     SEC-07 (Coolify <c>ThrottlesManualWebhookFailures</c> parity): failed webhook authentications are counted per
///     provider, client address, repository and branch, so one misconfigured repository behind a Git host's shared
///     egress address cannot lock out deliveries for other repositories. Within one window only <b>distinct</b>
///     failed attempts count (the same wrong token, or a redelivery of the same signed payload, counts once). After
///     <see cref="MaxFailures" /> distinct failures the scope is locked and further failures are answered with 429,
///     but correctly signed deliveries are still processed. Attempts are stored only as keyed hashes.
/// </summary>
public sealed class WebhookFailureThrottle(IRedisCache cache, TimeProvider timeProvider)
{
    public const int MaxFailures = 30;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private const string Prefix = "vessel:webhook-failures:";

    // Per-process key for attempt markers: raw tokens/signatures are never written to the cache.
    private static readonly byte[] MarkerKey = RandomNumberGenerator.GetBytes(32);

    public static string ScopeKey(WebhookProvider provider, string? clientAddress, string? repository, string? branch)
    {
        var scope = JsonSerializer.Serialize(new[] { repository?.ToLowerInvariant(), branch });
        return string.Create(CultureInfo.InvariantCulture,
            $"{provider.ToString().ToLowerInvariant()}:{clientAddress ?? "unknown"}:{Sha256(scope)}");
    }

    /// <summary>
    ///     What the secret check depends on: the static token (GitLab, generic), or the pair (payload, signature) for
    ///     HMAC providers. A repeat of a known attempt does not test a new secret.
    /// </summary>
    public static string AttemptIdentity(WebhookProvider provider, string? suppliedCredential, string rawBody)
    {
        return provider switch
        {
            WebhookProvider.GitLab or WebhookProvider.Generic => "token:" + (suppliedCredential ?? string.Empty),
            _ => "hmac:" + Sha256(rawBody) + ":" + (suppliedCredential ?? string.Empty)
        };
    }

    public async Task<bool> IsLockedAsync(string scopeKey, CancellationToken cancellationToken = default)
    {
        var stored = await cache.GetStringAsync(CounterKey(scopeKey), cancellationToken);
        return long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
               && count >= MaxFailures;
    }

    /// <summary>Counts a failed attempt unless the same attempt was already counted in this window.</summary>
    public async Task RecordFailureAsync(string scopeKey, string attempt, CancellationToken cancellationToken = default)
    {
        var marker = Convert.ToHexString(HMACSHA256.HashData(MarkerKey, Encoding.UTF8.GetBytes(attempt)))
            .ToLowerInvariant();
        TimeSpan ttl = Window * 2;
        var seen = await cache.IncrementAsync($"{CounterKey(scopeKey)}:seen:{marker}", ttl, cancellationToken);
        if (seen > 1) return;

        await cache.IncrementAsync(CounterKey(scopeKey), ttl, cancellationToken);
    }

    public TimeSpan RetryAfter()
    {
        var windowSeconds = (long)Window.TotalSeconds;
        var elapsed = timeProvider.GetUtcNow().ToUnixTimeSeconds() % windowSeconds;
        return TimeSpan.FromSeconds(Math.Max(1, windowSeconds - elapsed));
    }

    private string CounterKey(string scopeKey)
    {
        var window = timeProvider.GetUtcNow().ToUnixTimeSeconds() / (long)Window.TotalSeconds;
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{scopeKey}:{window}");
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
