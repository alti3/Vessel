namespace Vessel.Web.Configuration;

public sealed class DiagnosticsOptions
{
    public const string SectionName = "Vessel:Diagnostics";

    public bool OpenTelemetryEnabled { get; init; } = true;

    public string? OtlpEndpoint { get; init; }
}
