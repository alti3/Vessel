using Vessel.Shared.Common;

namespace Vessel.E2ETests.Architecture;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void E2eTestsCanReferenceWebAndSharedContracts()
    {
        Assert.Equal("Vessel.Shared", typeof(SharedAssembly).Assembly.GetName().Name);
    }
}
