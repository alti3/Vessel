using Microsoft.Extensions.Options;

namespace Vessel.Web.Configuration;

public sealed class VesselHostOptionsValidator : IValidateOptions<VesselHostOptions>
{
    public ValidateOptionsResult Validate(string? name, VesselHostOptions options)
    {
        return string.IsNullOrWhiteSpace(options.ServiceName)
            ? ValidateOptionsResult.Fail("Host:ServiceName is required.")
            : ValidateOptionsResult.Success;
    }
}

public sealed class DiagnosticsOptionsValidator : IValidateOptions<DiagnosticsOptions>
{
    public ValidateOptionsResult Validate(string? name, DiagnosticsOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.OtlpEndpoint)
            && !Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out _)
            ? ValidateOptionsResult.Fail("Diagnostics:OtlpEndpoint must be an absolute URI.")
            : ValidateOptionsResult.Success;
    }
}

public sealed class SecurityHeadersOptionsValidator : IValidateOptions<SecurityHeadersOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityHeadersOptions options)
    {
        return options.Enabled && string.IsNullOrWhiteSpace(options.ContentSecurityPolicy)
            ? ValidateOptionsResult.Fail("SecurityHeaders:ContentSecurityPolicy is required when security headers are enabled.")
            : ValidateOptionsResult.Success;
    }
}

public sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        List<string> failures = [];

        ValidatePolicy("Auth", options.Auth, failures);
        ValidatePolicy("Webhooks", options.Webhooks, failures);
        ValidatePolicy("Api", options.Api, failures);
        ValidatePolicy("Terminal", options.Terminal, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePolicy(
        string name,
        RateLimitPolicyOptions options,
        List<string> failures)
    {
        if (options.PermitLimit is < 1 or > 100_000)
        {
            failures.Add($"RateLimits:{name}:PermitLimit must be between 1 and 100000.");
        }

        if (options.WindowSeconds is < 1 or > 86_400)
        {
            failures.Add($"RateLimits:{name}:WindowSeconds must be between 1 and 86400.");
        }
    }
}
