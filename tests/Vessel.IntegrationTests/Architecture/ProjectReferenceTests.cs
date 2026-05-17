using Vessel.Application.Common;
using Vessel.Domain.Common;
using Vessel.Infrastructure.Common;

namespace Vessel.IntegrationTests.Architecture;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void IntegrationTestsCanReferenceApplicationDomainAndInfrastructure()
    {
        Assert.Equal("Vessel.Application", typeof(ApplicationAssembly).Assembly.GetName().Name);
        Assert.Equal("Vessel.Domain", typeof(DomainAssembly).Assembly.GetName().Name);
        Assert.Equal("Vessel.Infrastructure", typeof(InfrastructureAssembly).Assembly.GetName().Name);
    }
}
