using Vessel.Shared.Configuration;

namespace Vessel.UnitTests.Configuration;

public sealed class VesselEnvironmentNamesTests
{
    [Theory]
    [InlineData(VesselEnvironmentNames.Development)]
    [InlineData(VesselEnvironmentNames.Staging)]
    [InlineData(VesselEnvironmentNames.Production)]
    [InlineData(VesselEnvironmentNames.Testing)]
    public void IsKnownReturnsTrueForSupportedEnvironmentNames(string environmentName)
    {
        Assert.True(VesselEnvironmentNames.IsKnown(environmentName));
    }

    [Fact]
    public void IsKnownReturnsFalseForUnsupportedEnvironmentName()
    {
        Assert.False(VesselEnvironmentNames.IsKnown("Local"));
    }
}
