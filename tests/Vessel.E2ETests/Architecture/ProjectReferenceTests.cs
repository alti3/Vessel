using Vessel.Shared.Common;

namespace Vessel.E2ETests.Architecture;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void E2e_tests_can_reference_web_and_shared_contracts()
    {
        Assert.Equal("Vessel.Shared", typeof(SharedAssembly).Assembly.GetName().Name);
    }
}
