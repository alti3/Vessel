namespace Vessel.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Vessel:Database";

    public bool Enabled { get; init; }

    public string? ConnectionString { get; init; }

    public int TimeoutSeconds { get; init; } = 5;
}
