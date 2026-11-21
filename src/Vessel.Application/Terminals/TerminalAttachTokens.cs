using System.Collections.Concurrent;
using System.Security.Cryptography;
using Vessel.Domain;

namespace Vessel.Application.Terminals;

/// <summary>
///     Short-lived, single-use terminal attach tokens (SRV-06, upstream Coolify <c>TerminalSessionService::issue</c>).
///     A token is bound to the user, the active team and the session; only its SHA-256 hash is kept, in memory, because
///     terminal processes are node-local as well. Redeeming consumes the token whether or not it matches the binding.
/// </summary>
public sealed class TerminalAttachTokens(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public (string Token, DateTimeOffset ExpiresAt) Issue(UserId userId, TeamId teamId, TerminalSessionId sessionId)
    {
        Sweep();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        DateTimeOffset expiresAt = timeProvider.GetUtcNow().Add(Lifetime);
        _entries[Hash(token)] = new Entry(userId, teamId, sessionId, expiresAt);
        return (token, expiresAt);
    }

    /// <summary>Consumes <paramref name="token" />; true only when it is unexpired and bound to exactly this user, team and session.</summary>
    public bool TryRedeem(string? token, UserId userId, TeamId teamId, TerminalSessionId sessionId)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return false;
        if (!_entries.TryRemove(Hash(token), out Entry? entry)) return false;
        return entry.ExpiresAt > timeProvider.GetUtcNow() &&
               entry.UserId == userId &&
               entry.TeamId == teamId &&
               entry.SessionId == sessionId;
    }

    public void Revoke(TerminalSessionId sessionId)
    {
        foreach (var (hash, entry) in _entries)
            if (entry.SessionId == sessionId)
                _entries.TryRemove(hash, out _);
    }

    private void Sweep()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (var (hash, entry) in _entries)
            if (entry.ExpiresAt <= now)
                _entries.TryRemove(hash, out _);
    }

    private static string Hash(string token)
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    private sealed record Entry(UserId UserId, TeamId TeamId, TerminalSessionId SessionId, DateTimeOffset ExpiresAt);
}
