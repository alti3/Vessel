namespace Vessel.Infrastructure.Configuration;

public sealed class RedisOptions
{
    public const string SectionName = "Vessel:Redis";

    public bool Enabled { get; init; }

    public string? ConnectionString { get; init; }

    public int TimeoutSeconds { get; init; } = 5;
}
