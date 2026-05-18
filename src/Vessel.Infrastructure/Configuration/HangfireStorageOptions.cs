namespace Vessel.Infrastructure.Configuration;

public sealed class HangfireStorageOptions
{
    public const string SectionName = "Vessel:Hangfire";

    public bool Enabled { get; init; }

    public string StorageProvider { get; init; } = "PostgreSql";

    public string? ConnectionString { get; init; }

    public int TimeoutSeconds { get; init; } = 5;
}
