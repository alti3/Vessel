namespace Vessel.Shared.Configuration;

public static class VesselEnvironmentNames
{
    public const string Development = "Development";
    public const string Staging = "Staging";
    public const string Production = "Production";
    public const string Testing = "Testing";

    public static bool IsKnown(string environmentName)
    {
        return string.Equals(environmentName, Development, StringComparison.Ordinal)
               || string.Equals(environmentName, Staging, StringComparison.Ordinal)
               || string.Equals(environmentName, Production, StringComparison.Ordinal)
               || string.Equals(environmentName, Testing, StringComparison.Ordinal);
    }
}
