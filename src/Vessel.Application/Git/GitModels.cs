namespace Vessel.Application.Git;

public sealed record GitRepositoryRef(string Name, string Sha, bool IsTag);

public sealed record GitCommitInfo(
    string Sha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject);

public sealed record GitCloneRequest(
    Uri RepositoryUrl,
    string TargetDirectory,
    string? BranchOrTag = null,
    int Depth = 1,
    bool RecurseSubmodules = false,
    IReadOnlyDictionary<string, string?>? Environment = null);

/// <summary>How <see cref="GitCheckoutRequest.Reference" /> is resolved by <see cref="IGitClient.CheckoutAsync(GitCheckoutRequest, CancellationToken)" />.</summary>
public enum GitCheckoutKind
{
    /// <summary>A branch or tag name; the remote resolves it (shallow fetch of the named ref).</summary>
    Named = 0,

    /// <summary>
    ///     A full or abbreviated commit SHA. Full SHAs are fetched directly (shallow); when the server refuses a fetch by
    ///     SHA, or the SHA is abbreviated, <see cref="GitCheckoutRequest.FallbackBranch" /> is fetched and the commit is
    ///     checked out from it.
    /// </summary>
    Commit = 1,

    /// <summary>
    ///     A provider pull/merge request ref (for example <c>refs/pull/7/head</c>) or, for providers without PR refs, the
    ///     source branch. When <see cref="GitCheckoutRequest.ExpectedCommitSha" /> is set that exact commit is checked out.
    /// </summary>
    PullRequest = 2
}

/// <summary>
///     Non-interactive, bounded checkout of exactly one revision into an empty owned directory
///     (<c>git init</c>, <c>git fetch --depth 1 origin &lt;ref&gt;</c>, <c>git checkout --detach</c>).
/// </summary>
public sealed record GitCheckoutRequest(
    Uri RepositoryUrl,
    string TargetDirectory,
    GitCheckoutKind Kind,
    string Reference,
    string? FallbackBranch = null,
    string? ExpectedCommitSha = null,
    int Depth = 1,
    bool RecurseSubmodules = false,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? FetchTimeout = null);
