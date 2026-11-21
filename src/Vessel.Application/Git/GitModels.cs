using Vessel.Domain.Sources;

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
    IReadOnlyDictionary<string, string?>? Environment = null,
    GitAuthentication? Authentication = null);

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
    TimeSpan? FetchTimeout = null,
    GitAuthentication? Authentication = null);

/// <summary>
///     Credentials and host trust for one git operation (DEP-08). Infrastructure materializes them in a private
///     temporary directory for the lifetime of the operation: HTTPS credentials through a <c>GIT_ASKPASS</c> helper
///     that answers only for <see cref="GitHttpsCredential.Host" /> (never in the URL, arguments or logs), SSH keys as
///     a <c>0600</c> identity file, and host keys as a dedicated <c>known_hosts</c> file.
/// </summary>
public sealed record GitAuthentication(
    GitHttpsCredential? Https = null,
    GitSshCredential? Ssh = null,
    GitSshHostTrust? HostTrust = null,
    string? Description = null)
{
    public static GitAuthentication None { get; } = new();

    /// <summary>Secret values to redact from process output and errors.</summary>
    public IReadOnlyList<string> SecretValues =>
        Https is null ? [] : [Https.Password];
}

/// <summary>HTTPS username and password/token. <see cref="Host" /> (host or host:port) binds the credential.</summary>
public sealed record GitHttpsCredential(string Host, string Username, string Password)
{
    public override string ToString()
    {
        return $"GitHttpsCredential {{ Host = {Host}, Username = {Username}, Password = [redacted] }}";
    }
}

/// <summary>Private key used as SSH identity (deploy key).</summary>
public sealed record GitSshCredential(string PrivateKey)
{
    public override string ToString()
    {
        return "GitSshCredential { PrivateKey = [redacted] }";
    }
}

/// <summary>
///     SSH host trust for <see cref="Host" />:<see cref="Port" />. Connections accept only
///     <see cref="KnownHostLines" />; when there are none and <see cref="AllowFirstUse" /> is set the first key seen is
///     accepted (trust on first use) and reported to <see cref="OnFirstUse" /> to be pinned.
/// </summary>
public sealed record GitSshHostTrust(
    string Host,
    int Port,
    IReadOnlyList<string> KnownHostLines,
    bool AllowFirstUse,
    Func<IReadOnlyList<KnownHostsEntry>, CancellationToken, Task>? OnFirstUse = null);
