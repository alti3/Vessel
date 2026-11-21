using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Applications;

public readonly record struct GitSource(
    RepositoryUrl RepositoryUrl,
    string Branch,
    string? CommitSha)
{
    public GitSource(RepositoryUrl repositoryUrl, string branch)
        : this(repositoryUrl, string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(), null)
    {
    }

    /// <summary>
    ///     Builds a source from user input (API, UI, import) and validates every part at the boundary (SEC-10): the
    ///     repository URL (no embedded credentials), the branch as a <see cref="GitRef" /> and the pinned commit as a
    ///     commit SHA or ref. Persisted values are validated again when a deployment is queued and when it starts.
    /// </summary>
    public static GitSource FromInput(string? repositoryUrl, string? branch, string? commitSha = null)
    {
        RepositoryUrl url = RepositoryUrl.ParseInput(repositoryUrl);
        var normalizedBranch = string.IsNullOrWhiteSpace(branch)
            ? "main"
            : GitRef.Parse(branch, "branch").Value;
        string? commit = null;
        if (!string.IsNullOrWhiteSpace(commitSha) &&
            !string.Equals(commitSha.Trim(), "HEAD", StringComparison.Ordinal))
            commit = GitRef.Parse(commitSha, "commit").Value;
        return new GitSource(url, normalizedBranch, commit);
    }

    /// <summary>Re-validates a persisted source before it reaches git (queue time and deployment start).</summary>
    public void EnsureSafeForGit()
    {
        if (!GitRef.TryParse(Branch, out _, out var error))
            throw new DomainException($"Invalid application branch: {error}");
        if (CommitSha is not null && CommitSha != "HEAD" && !GitRef.TryParse(CommitSha, out _, out error))
            throw new DomainException($"Invalid application commit: {error}");
    }
}
