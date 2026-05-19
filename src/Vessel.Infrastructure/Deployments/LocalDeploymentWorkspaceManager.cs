using System.Text;
using Vessel.Application.Deployments;
using Vessel.Application.Files;
using Vessel.Domain;

namespace Vessel.Infrastructure.Deployments;

public sealed class LocalDeploymentWorkspaceManager(IPathSafetyService paths) : IDeploymentWorkspaceManager
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "storage", "deployments");

    public Task<DeploymentWorkspace> PrepareAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default)
    {
        string root = paths.EnsureOwnedPath(_root, Path.Combine(_root, deploymentId.Value.ToString("D")));
        Directory.CreateDirectory(root);
        string repository = paths.EnsureOwnedPath(root, Path.Combine(root, "repository"));
        Directory.CreateDirectory(repository);

        return Task.FromResult(new DeploymentWorkspace(
            root,
            repository,
            Path.Combine(root, "docker-compose.yml"),
            Path.Combine(root, ".env")));
    }

    public async Task WriteTextAsync(
        string rootDirectory,
        string relativePath,
        string contents,
        bool restrictToOwner,
        CancellationToken cancellationToken = default)
    {
        string path = paths.EnsureOwnedRelativePath(rootDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), cancellationToken);

        if (restrictToOwner && !OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
    }

    public Task<string> ReadTextAsync(
        string rootDirectory,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        string path = paths.EnsureOwnedRelativePath(rootDirectory, relativePath);
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    public Task CleanupAsync(DeploymentId deploymentId, CancellationToken cancellationToken = default)
    {
        string root = paths.EnsureOwnedPath(_root, Path.Combine(_root, deploymentId.Value.ToString("D")));
        string repository = Path.Combine(root, "repository");
        if (Directory.Exists(repository))
            Directory.Delete(repository, recursive: true);

        return Task.CompletedTask;
    }
}
