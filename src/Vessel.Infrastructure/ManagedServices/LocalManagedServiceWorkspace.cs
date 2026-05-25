using Vessel.Application.Files;
using Vessel.Application.ManagedServices;
using Vessel.Domain;

namespace Vessel.Infrastructure.ManagedServices;

public sealed class LocalManagedServiceWorkspace(IPathSafetyService paths) : IManagedServiceWorkspace
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "storage", "managed-services");

    public Task<ManagedServiceWorkspace> PrepareDatabaseAsync(
        DatabaseResourceId databaseId,
        CancellationToken cancellationToken = default)
    {
        return PrepareAsync("databases", databaseId.Value, cancellationToken);
    }

    public Task<ManagedServiceWorkspace> PrepareServiceAsync(
        ServiceResourceId serviceId,
        CancellationToken cancellationToken = default)
    {
        return PrepareAsync("services", serviceId.Value, cancellationToken);
    }

    public async Task WriteTextAsync(
        string rootDirectory,
        string relativePath,
        string contents,
        bool restrictToOwner,
        CancellationToken cancellationToken = default)
    {
        string root = paths.EnsureOwnedPath(_root, rootDirectory);
        string destination = paths.EnsureOwnedRelativePath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, contents, cancellationToken);
        _ = restrictToOwner;
    }

    private Task<ManagedServiceWorkspace> PrepareAsync(string kind, Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.Combine(_root, kind, id.ToString("N"));
        string safeRoot = paths.EnsureOwnedPath(_root, root);
        Directory.CreateDirectory(safeRoot);
        return Task.FromResult(new ManagedServiceWorkspace(
            safeRoot,
            Path.Combine(safeRoot, "docker-compose.yml"),
            Path.Combine(safeRoot, ".env")));
    }
}
