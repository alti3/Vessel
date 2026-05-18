namespace Vessel.Web.Configuration;

public sealed class VesselHostOptions
{
    public const string SectionName = "Vessel:Host";

    public string ServiceName { get; init; } = "Vessel.Web";
}
