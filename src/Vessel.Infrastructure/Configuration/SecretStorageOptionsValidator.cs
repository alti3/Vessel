using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Vessel.Infrastructure.Security;
using Vessel.Shared.Configuration;

namespace Vessel.Infrastructure.Configuration;

/// <summary>
/// Fails startup outside Development when no secret encryption key is configured, and rejects malformed
/// keys, key labels, or key files with unsafe permissions.
/// </summary>
public sealed class SecretStorageOptionsValidator(bool isDevelopment) : IValidateOptions<SecretStorageOptions>
{
    /// <summary>Host configuration key holding the environment name (same key the .NET host uses).</summary>
    public const string EnvironmentConfigurationKey = "environment";

    public ValidateOptionsResult Validate(string? name, SecretStorageOptions options)
    {
        IReadOnlyList<string> errors = SecretKeyRing.Validate(options, isDevelopment);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// Resolves the host environment the same way the .NET host does (defaulting to Production), so an
    /// unset environment is treated as production and requires a configured key.
    /// </summary>
    public static bool IsDevelopment(IConfiguration configuration)
    {
        string environment = configuration[EnvironmentConfigurationKey] ?? VesselEnvironmentNames.Production;
        return string.Equals(environment, VesselEnvironmentNames.Development, StringComparison.OrdinalIgnoreCase);
    }
}
