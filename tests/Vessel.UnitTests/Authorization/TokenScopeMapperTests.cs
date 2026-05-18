using Vessel.Application.Authorization;

namespace Vessel.UnitTests.Authorization;

public sealed class TokenScopeMapperTests
{
    [Fact]
    public void ReadSensitiveScopeIncludesSecretReadOnlyWhenReadIsAlsoPresent()
    {
        IReadOnlySet<string> permissions = TokenScopeMapper.ToPermissions(["read", "read:sensitive"]);

        Assert.Contains(VesselPermissions.ProjectsRead, permissions);
        Assert.Contains(VesselPermissions.SecretsRead, permissions);
        Assert.DoesNotContain(VesselPermissions.SecretsWrite, permissions);
    }

    [Fact]
    public void RootScopeMapsToEveryPermission()
    {
        IReadOnlySet<string> permissions = TokenScopeMapper.ToPermissions(["root"]);

        Assert.True(VesselPermissions.All.IsSubsetOf(permissions));
    }
}
