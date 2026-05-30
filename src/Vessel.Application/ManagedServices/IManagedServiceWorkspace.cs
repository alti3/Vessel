using Vessel.Domain;

namespace Vessel.Application.ManagedServices;

public sealed record ManagedServiceWorkspace(string RootDirectory, string ComposeFilePath, string EnvironmentFilePath);

public interface IManagedServiceWorkspace
{
    Task<ManagedServiceWorkspace> PrepareDatabaseAsync(
        DatabaseResourceId databaseId,
        CancellationToken cancellationToken = default);

    Task<ManagedServiceWorkspace> PrepareServiceAsync(
        ServiceResourceId serviceId,
        CancellationToken cancellationToken = default);

    Task WriteTextAsync(
        string rootDirectory,
        string relativePath,
        string contents,
        bool restrictToOwner,
        CancellationToken cancellationToken = default);
}
