namespace Vessel.Application.Git;

public interface IGitClient
{
    Task CloneAsync(GitCloneRequest request, CancellationToken cancellationToken = default);

    Task FetchAsync(string repositoryDirectory, string? remote, CancellationToken cancellationToken = default);

    Task CheckoutAsync(string repositoryDirectory, string reference, CancellationToken cancellationToken = default);

    Task<GitCommitInfo> GetCommitAsync(string repositoryDirectory, string reference,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitRepositoryRef>> ListRefsAsync(Uri repositoryUrl,
        CancellationToken cancellationToken = default);
}
