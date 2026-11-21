namespace Vessel.Application.Git;

public interface IGitClient
{
    Task CloneAsync(GitCloneRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Checks out exactly one revision (branch, tag, commit SHA or pull request ref) without interactive prompts.
    ///     References are validated before any git process starts.
    /// </summary>
    Task CheckoutAsync(GitCheckoutRequest request, CancellationToken cancellationToken = default);

    Task FetchAsync(string repositoryDirectory, string? remote, CancellationToken cancellationToken = default);

    Task CheckoutAsync(string repositoryDirectory, string reference, CancellationToken cancellationToken = default);

    Task<GitCommitInfo> GetCommitAsync(string repositoryDirectory, string reference,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitRepositoryRef>> ListRefsAsync(Uri repositoryUrl,
        CancellationToken cancellationToken = default);

    /// <summary>Lists remote refs with the credentials and host trust of a Git source (DEP-08).</summary>
    Task<IReadOnlyList<GitRepositoryRef>> ListRefsAsync(Uri repositoryUrl, GitAuthentication? authentication,
        CancellationToken cancellationToken = default)
    {
        if (authentication is not null && !ReferenceEquals(authentication, GitAuthentication.None))
            throw new NotSupportedException("This git client does not support authenticated remotes.");
        return ListRefsAsync(repositoryUrl, cancellationToken);
    }
}
