using Vessel.Application.Deployments;
using Vessel.Domain;
using Vessel.Infrastructure.Deployments;
using Vessel.Infrastructure.Files;

namespace Vessel.IntegrationTests.Security;

/// <summary>SEC-05: real-path containment and symlink-free repository reads / workspace writes.</summary>
public sealed class SymlinkContainmentTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "vessel-symlink-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly PathSafetyService _paths = new();

    public SymlinkContainmentTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_base, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "host-secret");
    }

    [Fact]
    public void SymlinkedFileEscapingRoot_IsRejected()
    {
        if (!TryLink(Path.Combine(_root, "docker-compose.yml"), Path.Combine(_outside, "secret.txt"))) return;

        Assert.Throws<InvalidOperationException>(() => _paths.EnsureOwnedRelativePath(_root, "docker-compose.yml"));
        Assert.Throws<InvalidOperationException>(() => _paths.EnsureNoSymlinks(_root, "docker-compose.yml"));
    }

    [Fact]
    public void SymlinkedParentDirectoryEscapingRoot_IsRejected()
    {
        if (!TryLink(Path.Combine(_root, "config"), _outside, directory: true)) return;

        Assert.Throws<InvalidOperationException>(() => _paths.EnsureOwnedRelativePath(_root, "config/secret.txt"));
        Assert.Throws<InvalidOperationException>(() =>
            _paths.EnsureOwnedPath(_root, Path.Combine(_root, "config", "new-file.txt")));
    }

    [Fact]
    public void LinkTargetWithDotDotIsResolvedPhysically()
    {
        // inside/deep -> outside dir; trick -> "deep/../.." looks lexically like the root but resolves outside.
        Directory.CreateDirectory(Path.Combine(_outside, "a"));
        if (!TryLink(Path.Combine(_root, "deep"), Path.Combine(_outside, "a"), directory: true)) return;
        TryLink(Path.Combine(_root, "trick"), Path.Combine("deep", "..", "secret.txt"));

        Assert.Throws<InvalidOperationException>(() => _paths.EnsureOwnedRelativePath(_root, "trick"));
    }

    [Fact]
    public void SymlinkInsideRoot_IsContainedButStillRejectedForUntrustedReads()
    {
        File.WriteAllText(Path.Combine(_root, "real.yml"), "services: {}");
        if (!TryLink(Path.Combine(_root, "alias.yml"), Path.Combine(_root, "real.yml"))) return;

        Assert.Equal(Path.Combine(_root, "alias.yml"), _paths.EnsureOwnedRelativePath(_root, "alias.yml"));
        Assert.Throws<InvalidOperationException>(() => _paths.EnsureNoSymlinks(_root, "alias.yml"));
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("a/../../outside/secret.txt")]
    [InlineData("./../x")]
    public void TraversalSegments_AreRejected(string relative)
    {
        Assert.Throws<InvalidOperationException>(() => _paths.EnsureNoSymlinks(_root, relative));
    }

    [Fact]
    public async Task WorkspaceRead_RejectsSymlinkedComposeFileFromRepository()
    {
        var manager = new LocalDeploymentWorkspaceManager(_paths);
        DeploymentWorkspace workspace = await manager.PrepareAsync(DeploymentId.New(), TestContext.Current.CancellationToken);
        try
        {
            if (!TryLink(Path.Combine(workspace.RepositoryDirectory, "docker-compose.yml"),
                    Path.Combine(_outside, "secret.txt"))) return;
            TryLink(Path.Combine(workspace.RepositoryDirectory, "Dockerfile"), Path.Combine(_outside, "secret.txt"));
            File.WriteAllText(Path.Combine(workspace.RepositoryDirectory, "ok.yml"), "services: {}");

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadTextAsync(
                workspace.RepositoryDirectory, "docker-compose.yml", TestContext.Current.CancellationToken));
            Assert.Throws<InvalidOperationException>(() =>
                manager.EnsureRegularFile(workspace.RepositoryDirectory, "Dockerfile"));
            Assert.Equal("services: {}", await manager.ReadTextAsync(workspace.RepositoryDirectory, "ok.yml",
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<FileNotFoundException>(() => manager.ReadTextAsync(
                workspace.RepositoryDirectory, "missing.yml", TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(workspace.RootDirectory, true);
        }
    }

    [Fact]
    public async Task WorkspaceWrite_NeverWritesThroughSymlinks_AndRestrictsSecrets()
    {
        var manager = new LocalDeploymentWorkspaceManager(_paths);
        DeploymentWorkspace workspace = await manager.PrepareAsync(DeploymentId.New(), TestContext.Current.CancellationToken);
        try
        {
            await manager.WriteTextAsync(workspace.RootDirectory, ".env", "A=1", true, TestContext.Current.CancellationToken);
            await manager.WriteTextAsync(workspace.RootDirectory, ".env", "A=2", true, TestContext.Current.CancellationToken);
            Assert.Equal("A=2", await File.ReadAllTextAsync(Path.Combine(workspace.RootDirectory, ".env"),
                TestContext.Current.CancellationToken));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(workspace.RootDirectory, ".env")));
            Assert.Single(Directory.GetFiles(workspace.RootDirectory));

            if (!TryLink(Path.Combine(workspace.RootDirectory, "snapshots"), _outside, directory: true)) return;
            TryLink(Path.Combine(workspace.RootDirectory, "docker-compose.yml"), Path.Combine(_outside, "secret.txt"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteTextAsync(workspace.RootDirectory,
                "snapshots/docker-compose.redacted.yml", "x", false, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteTextAsync(workspace.RootDirectory,
                "docker-compose.yml", "x", false, TestContext.Current.CancellationToken));
            Assert.Equal("host-secret", await File.ReadAllTextAsync(Path.Combine(_outside, "secret.txt"),
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(_outside, "docker-compose.redacted.yml")));
        }
        finally
        {
            Directory.Delete(workspace.RootDirectory, true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, true);
    }

    /// <summary>Creates a symlink; returns false where the platform/user cannot (e.g. unprivileged Windows).</summary>
    private static bool TryLink(string path, string target, bool directory = false)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
