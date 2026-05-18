using Microsoft.Extensions.Options;

namespace Vessel.Infrastructure.Configuration;

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        return ValidateConnectionOptions("Database", options.Enabled, options.ConnectionString, options.TimeoutSeconds);
    }

    internal static ValidateOptionsResult ValidateConnectionOptions(
        string optionsName,
        bool enabled,
        string? connectionString,
        int timeoutSeconds)
    {
        List<string> failures = [];

        if (enabled && string.IsNullOrWhiteSpace(connectionString))
            failures.Add($"{optionsName}:ConnectionString is required when {optionsName}:Enabled is true.");

        if (timeoutSeconds is < 1 or > 300) failures.Add($"{optionsName}:TimeoutSeconds must be between 1 and 300.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class RedisOptionsValidator : IValidateOptions<RedisOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisOptions options)
    {
        return DatabaseOptionsValidator.ValidateConnectionOptions(
            "Redis",
            options.Enabled,
            options.ConnectionString,
            options.TimeoutSeconds);
    }
}

public sealed class HangfireStorageOptionsValidator : IValidateOptions<HangfireStorageOptions>
{
    private static readonly string[] SupportedStorageProviders = ["PostgreSql"];

    public ValidateOptionsResult Validate(string? name, HangfireStorageOptions options)
    {
        List<string> failures = [];

        if (options.Enabled
            && !SupportedStorageProviders.Contains(options.StorageProvider, StringComparer.OrdinalIgnoreCase))
            failures.Add("Hangfire:StorageProvider must be PostgreSql.");

        if (options.Enabled
            && string.IsNullOrWhiteSpace(options.ConnectionString))
            failures.Add("Hangfire:ConnectionString is required when Hangfire:Enabled is true.");

        if (options.TimeoutSeconds is < 1 or > 300) failures.Add("Hangfire:TimeoutSeconds must be between 1 and 300.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class ObjectStorageOptionsValidator : IValidateOptions<ObjectStorageOptions>
{
    private static readonly string[] SupportedProviders = ["S3", "Local"];

    public ValidateOptionsResult Validate(string? name, ObjectStorageOptions options)
    {
        List<string> failures = [];

        if (options.Enabled
            && !SupportedProviders.Contains(options.Provider, StringComparer.OrdinalIgnoreCase))
            failures.Add("ObjectStorage:Provider must be S3 or Local.");

        Uri? endpoint = null;
        bool isLocal = string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase);

        if (options.Enabled && !isLocal && !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out endpoint))
            failures.Add("ObjectStorage:Endpoint must be an absolute URI when ObjectStorage:Enabled is true.");

        if (options.Enabled
            && !isLocal
            && endpoint is not null
            && endpoint.Scheme is not "http" and not "https")
            failures.Add("ObjectStorage:Endpoint must use http or https.");

        if (options.Enabled && string.IsNullOrWhiteSpace(options.BucketName))
            failures.Add("ObjectStorage:BucketName is required when ObjectStorage:Enabled is true.");

        if (options.TimeoutSeconds is < 1 or > 300)
            failures.Add("ObjectStorage:TimeoutSeconds must be between 1 and 300.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
