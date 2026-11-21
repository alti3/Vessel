using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Databases;

/// <summary>
///     A pinned container image reference for a managed database: repository plus an explicit tag and/or digest.
///     Floating references (no tag, or the <c>latest</c> tag) are rejected because a stateful engine that silently
///     crosses a major version on recreate can refuse to start on its existing data directory.
/// </summary>
public readonly partial record struct DatabaseImage
{
    public const int MaxLength = 255;

    private DatabaseImage(string repository, string? tag, string? digest)
    {
        Repository = repository;
        Tag = tag;
        Digest = digest;
    }

    public string Repository { get; }

    public string? Tag { get; }

    public string? Digest { get; }

    public string Value => Repository + (Tag is null ? string.Empty : ":" + Tag) +
                           (Digest is null ? string.Empty : "@" + Digest);

    public static DatabaseImage Parse(string reference)
    {
        var value = DomainValidation.Required(reference, "Database image", MaxLength);
        Match match = ReferencePattern().Match(value);
        if (!match.Success)
            throw new DomainException(
                $"Database image '{value}' is not a valid image reference (repository[:tag][@sha256:digest]).");
        var tag = match.Groups["tag"].Success ? match.Groups["tag"].Value : null;
        var digest = match.Groups["digest"].Success ? match.Groups["digest"].Value : null;
        if (tag is null && digest is null)
            throw new DomainException(
                $"Database image '{value}' must name an explicit tag or digest; floating references are not allowed.");
        if (digest is null && string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(
                $"Database image '{value}' uses the 'latest' tag. Pin an explicit version so the database never " +
                "crosses a major version on recreate.");
        return new DatabaseImage(match.Groups["repository"].Value, tag, digest);
    }

    public static bool TryParse(string? reference, out DatabaseImage image)
    {
        try
        {
            image = Parse(reference ?? string.Empty);
            return true;
        }
        catch (DomainException)
        {
            image = default;
            return false;
        }
    }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex(
        @"^(?<repository>[a-z0-9]+(?:[._-][a-z0-9]+)*(?::[0-9]{1,5}(?=/))?(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*)(?::(?<tag>[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}))?(?:@(?<digest>sha256:[a-f0-9]{64}))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}

/// <summary>
///     Container health-check settings for a managed database. Defaults match upstream Coolify
///     (<c>add_health_check_to_standalone_databases</c>): enabled, 15 s interval, 5 s timeout, 5 retries, 5 s start
///     period.
/// </summary>
public readonly record struct DatabaseHealthCheckPolicy
{
    public const int DefaultIntervalSeconds = 15;

    public const int DefaultTimeoutSeconds = 5;

    public const int DefaultRetries = 5;

    public const int DefaultStartPeriodSeconds = 5;

    public DatabaseHealthCheckPolicy(bool enabled, int intervalSeconds, int timeoutSeconds, int retries,
        int startPeriodSeconds)
    {
        if (intervalSeconds is < 1 or > 3600)
            throw new DomainException("Health check interval must be between 1 and 3600 seconds.");
        if (timeoutSeconds is < 1 or > 3600)
            throw new DomainException("Health check timeout must be between 1 and 3600 seconds.");
        if (retries is < 1 or > 100) throw new DomainException("Health check retries must be between 1 and 100.");
        if (startPeriodSeconds is < 0 or > 3600)
            throw new DomainException("Health check start period must be between 0 and 3600 seconds.");
        Enabled = enabled;
        IntervalSeconds = intervalSeconds;
        TimeoutSeconds = timeoutSeconds;
        Retries = retries;
        StartPeriodSeconds = startPeriodSeconds;
    }

    public static DatabaseHealthCheckPolicy Default { get; } = new(true, DefaultIntervalSeconds,
        DefaultTimeoutSeconds, DefaultRetries, DefaultStartPeriodSeconds);

    public bool Enabled { get; }

    public int IntervalSeconds { get; }

    public int TimeoutSeconds { get; }

    public int Retries { get; }

    public int StartPeriodSeconds { get; }
}
