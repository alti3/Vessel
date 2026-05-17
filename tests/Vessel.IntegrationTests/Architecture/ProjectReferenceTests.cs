using Vessel.Application.Common;
using Vessel.Domain.Common;
using Vessel.Infrastructure.Common;

namespace Vessel.IntegrationTests.Architecture;

public sealed class ProjectReferenceTests
{
    [Fact]
    public void Integration_tests_can_reference_application_domain_and_infrastructure()
    {
        Assert.Equal("Vessel.Application", typeof(ApplicationAssembly).Assembly.GetName().Name);
        Assert.Equal("Vessel.Domain", typeof(DomainAssembly).Assembly.GetName().Name);
        Assert.Equal("Vessel.Infrastructure", typeof(InfrastructureAssembly).Assembly.GetName().Name);
    }
}
