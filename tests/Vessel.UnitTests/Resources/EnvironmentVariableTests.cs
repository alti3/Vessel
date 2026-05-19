using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.EnvironmentVariables;

namespace Vessel.UnitTests.Resources;

public sealed class EnvironmentVariableTests
{
    [Theory]
    [InlineData("DATABASE_URL")]
    [InlineData("_TOKEN")]
    [InlineData("BUILD_1")]
    public void EnvironmentVariableKeyAcceptsShellCompatibleNames(string key)
    {
        var parsed = new EnvironmentVariableKey(key);

        Assert.Equal(key, parsed.Value);
    }

    [Theory]
    [InlineData("1BAD")]
    [InlineData("BAD-NAME")]
    [InlineData("")]
    public void EnvironmentVariableKeyRejectsUnsafeNames(string key)
    {
        Assert.Throws<DomainException>(() => new EnvironmentVariableKey(key));
    }

    [Fact]
    public void SecretVariablesRequireSecretReference()
    {
        Assert.Throws<DomainException>(() => EnvironmentVariable.Create(
            TeamId.New(),
            EnvironmentVariableTargetType.Application,
            new EnvironmentVariableKey("DATABASE_URL"),
            EnvironmentVariableValueKind.Secret,
            null,
            null,
            true,
            true,
            false,
            false,
            false,
            null,
            DateTimeOffset.UtcNow));
    }
}
