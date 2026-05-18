using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

public sealed class ValueObjectTests
{
    [Fact]
    public void EmailAddress_NormalizesToLowercase()
    {
        var email = new EmailAddress("USER@Example.COM");

        Assert.Equal("user@example.com", email.Value);
    }

    [Fact]
    public void PortNumber_RejectsOutOfRangePort()
    {
        Assert.Throws<DomainException>(() => new PortNumber(70000));
    }

    [Fact]
    public void DomainName_RejectsInvalidDomain()
    {
        Assert.Throws<DomainException>(() => new DomainName("not a domain"));
    }
}
