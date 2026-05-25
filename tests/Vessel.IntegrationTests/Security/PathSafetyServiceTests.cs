using Vessel.Infrastructure.Files;

namespace Vessel.IntegrationTests.Security;

public sealed class PathSafetyServiceTests
{
    [Fact]
    public void EnsureOwnedRelativePath_AllowsPathInsideRoot()
    {
        var service = new PathSafetyService();
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var path = service.EnsureOwnedRelativePath(root, "deployments/app/config.yml");

        Assert.StartsWith(Path.GetFullPath(root), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureOwnedRelativePath_RejectsTraversal()
    {
        var service = new PathSafetyService();
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Throws<InvalidOperationException>(() => service.EnsureOwnedRelativePath(root, "../outside.txt"));
        Assert.Throws<InvalidOperationException>(() => service.EnsureOwnedRelativePath(root, "..\\outside.txt"));
        Assert.Throws<InvalidOperationException>(() => service.EnsureOwnedRelativePath(root, "\\outside.txt"));
        Assert.Throws<InvalidOperationException>(() => service.EnsureOwnedRelativePath(root, "C:\\outside.txt"));
    }
}
