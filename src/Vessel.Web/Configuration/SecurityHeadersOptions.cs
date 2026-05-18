namespace Vessel.Web.Configuration;

public sealed class SecurityHeadersOptions
{
    public const string SectionName = "Vessel:SecurityHeaders";

    public bool Enabled { get; init; } = true;

    public string ContentSecurityPolicy { get; init; } =
        "default-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'";
}
