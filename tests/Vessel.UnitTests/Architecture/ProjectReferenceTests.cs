using Vessel.Application.Common;
using Vessel.Domain.Common;

namespace Vessel.UnitTests.Architecture;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void UnitTestsCanReferenceDomainAndApplication()
    {
        Assert.Equal("Vessel.Domain", typeof(DomainAssembly).Assembly.GetName().Name);
        Assert.Equal("Vessel.Application", typeof(ApplicationAssembly).Assembly.GetName().Name);
    }
}
