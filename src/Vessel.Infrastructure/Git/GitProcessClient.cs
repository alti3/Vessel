using System.Globalization;
using Vessel.Application.Files;
using Vessel.Application.Git;
using Vessel.Application.Processes;
using Vessel.Domain.ValueObjects;

namespace Vessel.Infrastructure.Git;

public sealed class GitProcessClient(IProcessRunner processRunner, IPathSafetyService paths) : IGitClient
{
    private static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LocalCommandTimeout = TimeSpan.FromMinutes(2);

    public async Task CloneAsync(GitCloneRequest request, CancellationToken cancellationToken = default)
    {
        var target = paths.EnsureOwnedPath(Path.GetDirectoryName(Path.GetFullPath(request.TargetDirectory))!,
            request.TargetDirectory);
        var args = new List<string> { "clone", "--depth", request.Depth.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(request.BranchOrTag))
            args.AddRange(["--branch", GitRef.Parse(request.BranchOrTag, "branch or tag").Value]);
        if (request.RecurseSubmodules)
            args.AddRange(["--recurse-submodules", "--shallow-submodules"]);
        args.AddRange(["--", request.RepositoryUrl.ToString(), target]);

        using GitAuthenticationScope authentication = GitAuthenticationScope.Create(request.Authentication);
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "git",
            args,
            Environment: NonInteractiveEnvironment(request.Environment, authentication),
            Timeout: TimeSpan.FromMinutes(30),
            Redaction: Redaction(request.RepositoryUrl, authentication)), cancellationToken);
        ThrowIfFailed(result);
        await authentication.ReportFirstUseAsync(cancellationToken);
    }

    public async Task CheckoutAsync(GitCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        var target = PrepareTarget(request);

        Directory.CreateDirectory(target);
        if (Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidOperationException("Checkout target directory must be empty.");

        using GitAuthenticationScope authentication = GitAuthenticationScope.Create(request.Authentication);
        var context = new CheckoutContext(request, target, authentication);
        await RunAsync(context, ["init", "--quiet"], LocalCommandTimeout, cancellationToken);
        await RunAsync(context, ["remote", "add", "origin", "--", request.RepositoryUrl.ToString()],
            LocalCommandTimeout, cancellationToken);

        var revision = request.Kind switch
        {
            GitCheckoutKind.Commit => await FetchCommitAsync(context, cancellationToken),
            GitCheckoutKind.PullRequest => await FetchPullRequestAsync(context, cancellationToken),
            _ => await FetchNamedAsync(context, cancellationToken)
        };

        await RunAsync(context, ["checkout", "--quiet", "--force", "--detach", revision], LocalCommandTimeout,
            cancellationToken);

        if (request.RecurseSubmodules)
            await RunAsync(context, ["submodule", "update", "--init", "--recursive", "--depth", "1"], FetchTimeout(request),
                cancellationToken);
        await authentication.ReportFirstUseAsync(cancellationToken);
    }

    public async Task FetchAsync(string repositoryDirectory, string? remote,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "fetch", "--prune" };
        if (!string.IsNullOrWhiteSpace(remote)) args.Add(GitRef.Parse(remote, "remote").Value);
        ProcessResult result = await processRunner.RunTextAsync(Command(repositoryDirectory, args), cancellationToken);
        ThrowIfFailed(result);
    }

    public async Task CheckoutAsync(string repositoryDirectory, string reference,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result =
            await processRunner.RunTextAsync(Command(repositoryDirectory,
                    ["checkout", "--force", GitRef.Parse(reference, "checkout reference").Value]),
                cancellationToken);
        ThrowIfFailed(result);
    }

    public async Task<GitCommitInfo> GetCommitAsync(
        string repositoryDirectory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(Command(
            repositoryDirectory,
            ["show", "-s", "--format=%H%x00%an%x00%ae%x00%aI%x00%s", GitRef.Parse(reference, "commit reference").Value]),
            cancellationToken);
        ThrowIfFailed(result);
        var parts = result.StandardOutput.TrimEnd().Split('\0');
        return new GitCommitInfo(
            parts.ElementAtOrDefault(0) ?? string.Empty,
            parts.ElementAtOrDefault(1) ?? string.Empty,
            parts.ElementAtOrDefault(2) ?? string.Empty,
            DateTimeOffset.TryParse(parts.ElementAtOrDefault(3), CultureInfo.InvariantCulture,
                out DateTimeOffset authoredAt)
                ? authoredAt
                : DateTimeOffset.MinValue,
            parts.ElementAtOrDefault(4) ?? string.Empty);
    }

    public Task<IReadOnlyList<GitRepositoryRef>> ListRefsAsync(Uri repositoryUrl,
        CancellationToken cancellationToken = default)
    {
        return ListRefsAsync(repositoryUrl, null, cancellationToken);
    }

    public async Task<IReadOnlyList<GitRepositoryRef>> ListRefsAsync(Uri repositoryUrl,
        GitAuthentication? authentication, CancellationToken cancellationToken = default)
    {
        using GitAuthenticationScope scope = GitAuthenticationScope.Create(authentication);
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "git",
            ["ls-remote", "--heads", "--tags", "--", repositoryUrl.ToString()],
            Environment: NonInteractiveEnvironment(null, scope),
            Timeout: TimeSpan.FromMinutes(2),
            Redaction: Redaction(repositoryUrl, scope)), cancellationToken);
        ThrowIfFailed(result);
        await scope.ReportFirstUseAsync(cancellationToken);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseRef)
            .ToArray();
    }

    private string PrepareTarget(GitCheckoutRequest request)
    {
        ValidateCheckout(request);
        return paths.EnsureOwnedPath(Path.GetDirectoryName(Path.GetFullPath(request.TargetDirectory))!,
            request.TargetDirectory);
    }

    private async Task<string> FetchNamedAsync(CheckoutContext context, CancellationToken cancellationToken)
    {
        await FetchAsync(context, context.Request.Reference, context.Request.Depth, cancellationToken);
        return "FETCH_HEAD";
    }

    private async Task<string> FetchCommitAsync(CheckoutContext context, CancellationToken cancellationToken)
    {
        GitRef commit = GitRef.Parse(context.Request.Reference, "commit SHA");
        if (commit.IsFullCommitSha)
        {
            ProcessResult direct = await TryFetchAsync(context, commit.Value, context.Request.Depth, cancellationToken);
            if (direct.Succeeded) return commit.Value;
        }

        if (string.IsNullOrWhiteSpace(context.Request.FallbackBranch))
            throw new InvalidOperationException(
                $"Commit {commit.Value} could not be fetched directly and no branch was provided to search.");

        // The server refused a fetch by SHA (uploadpack.allowReachableSHA1InWant off) or the SHA is abbreviated:
        // fetch the branch history and resolve the commit locally.
        await FetchAsync(context, context.Request.FallbackBranch, null, cancellationToken);
        return await ResolveCommitAsync(context, commit.Value, cancellationToken);
    }

    private async Task<string> FetchPullRequestAsync(CheckoutContext context, CancellationToken cancellationToken)
    {
        await FetchAsync(context, context.Request.Reference, context.Request.Depth, cancellationToken);
        var expected = context.Request.ExpectedCommitSha;
        if (string.IsNullOrWhiteSpace(expected)) return "FETCH_HEAD";

        var fetchedHead = await RevParseAsync(context, "FETCH_HEAD", cancellationToken);
        if (fetchedHead.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return fetchedHead;

        // The pull request moved after the webhook was sent; deploy the commit the event referred to.
        GitRef commit = GitRef.Parse(expected, "pull request commit SHA");
        if (commit.IsFullCommitSha)
        {
            ProcessResult direct = await TryFetchAsync(context, commit.Value, context.Request.Depth, cancellationToken);
            if (direct.Succeeded) return commit.Value;
        }

        await FetchAsync(context, context.Request.Reference, null, cancellationToken);
        return await ResolveCommitAsync(context, commit.Value, cancellationToken);
    }

    private async Task<string> ResolveCommitAsync(CheckoutContext context, string commit,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunTextAsync(
            Command(context, ["rev-parse", "--verify", "--quiet", commit + "^{commit}"], LocalCommandTimeout),
            cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new InvalidOperationException($"Commit {commit} was not found in the fetched history.");
        return result.StandardOutput.Trim();
    }

    private async Task<string> RevParseAsync(CheckoutContext context, string revision,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunTextAsync(
            Command(context, ["rev-parse", "--verify", revision], LocalCommandTimeout), cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Trim();
    }

    private async Task FetchAsync(CheckoutContext context, string reference, int? depth,
        CancellationToken cancellationToken)
    {
        ThrowIfFailed(await TryFetchAsync(context, reference, depth, cancellationToken));
    }

    private Task<ProcessResult> TryFetchAsync(CheckoutContext context, string reference, int? depth,
        CancellationToken cancellationToken)
    {
        return processRunner.RunTextAsync(
            Command(context, FetchArguments(reference, depth), FetchTimeout(context.Request)),
            cancellationToken);
    }

    public static IReadOnlyList<string> FetchArguments(string reference, int? depth)
    {
        var args = new List<string> { "fetch", "--no-tags", "--no-recurse-submodules" };
        if (depth is > 0)
            args.AddRange(["--depth", depth.Value.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["origin", GitRef.Parse(reference, "fetch reference").Value]);
        return args;
    }

    private async Task RunAsync(CheckoutContext context, IReadOnlyList<string> args, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfFailed(await processRunner.RunTextAsync(Command(context, args, timeout), cancellationToken));
    }

    private static ProcessCommand Command(CheckoutContext context, IReadOnlyList<string> args, TimeSpan timeout)
    {
        return new ProcessCommand(
            "git",
            args,
            context.TargetDirectory,
            NonInteractiveEnvironment(context.Request.Environment, context.Authentication),
            timeout,
            Redaction: Redaction(context.Request.RepositoryUrl, context.Authentication));
    }

    public static void ValidateCheckout(GitCheckoutRequest request)
    {
        if (request.Depth < 1) throw new ArgumentOutOfRangeException(nameof(request), "Depth must be positive.");
        GitRef reference = GitRef.Parse(request.Reference, "git reference");
        if (request.Kind == GitCheckoutKind.Commit && !reference.IsCommitSha)
            throw new ArgumentException("Commit checkouts require a hexadecimal commit SHA.", nameof(request));
        if (request.FallbackBranch is not null) GitRef.Parse(request.FallbackBranch, "branch");
        if (request.ExpectedCommitSha is not null && !GitRef.IsCommitShaValue(request.ExpectedCommitSha))
            throw new ArgumentException("Expected commit must be a hexadecimal commit SHA.", nameof(request));
    }

    /// <summary>
    ///     Git must never prompt: no terminal credential prompts, no askpass helper and SSH in batch mode, so a missing
    ///     credential fails fast instead of hanging the deployment until its timeout.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> NonInteractiveEnvironment(
        IReadOnlyDictionary<string, string?>? environment)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes"
        };
        if (environment is not null)
            foreach (var (key, value) in environment)
                values[key] = value;
        values["GIT_TERMINAL_PROMPT"] = "0";
        return values;
    }

    /// <summary>
    ///     The non-interactive environment plus the credential and host-trust
    ///     entries of <paramref name="authentication" /> (DEP-08), which callers cannot override.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> NonInteractiveEnvironment(
        IReadOnlyDictionary<string, string?>? environment, GitAuthenticationScope authentication)
    {
        var values = new Dictionary<string, string?>(NonInteractiveEnvironment(environment), StringComparer.Ordinal);
        foreach (var (key, value) in authentication.Environment)
            values[key] = value;
        values["GIT_TERMINAL_PROMPT"] = "0";
        return values;
    }

    private static TimeSpan FetchTimeout(GitCheckoutRequest request)
    {
        return request.FetchTimeout ?? DefaultFetchTimeout;
    }

    private static ProcessRedactionProfile Redaction(Uri repositoryUrl, GitAuthenticationScope? authentication = null)
    {
        return new ProcessRedactionProfile([repositoryUrl.ToString(), .. authentication?.SecretValues ?? []], []);
    }

    private static ProcessCommand Command(string repositoryDirectory, IReadOnlyList<string> args)
    {
        return new ProcessCommand("git", args, repositoryDirectory, NonInteractiveEnvironment(null),
            TimeSpan.FromMinutes(5));
    }

    private static GitRepositoryRef ParseRef(string line)
    {
        var parts = line.Split('\t', 2);
        var name = parts.ElementAtOrDefault(1) ?? string.Empty;
        var isTag = name.StartsWith("refs/tags/", StringComparison.Ordinal);
        name = name.Replace("refs/heads/", string.Empty, StringComparison.Ordinal)
            .Replace("refs/tags/", string.Empty, StringComparison.Ordinal);
        return new GitRepositoryRef(name, parts.ElementAtOrDefault(0) ?? string.Empty, isTag);
    }

    private static void ThrowIfFailed(ProcessResult result)
    {
        if (!result.Succeeded) throw new ProcessExecutionException(result);
    }

    private sealed record CheckoutContext(
        GitCheckoutRequest Request,
        string TargetDirectory,
        GitAuthenticationScope Authentication);
}
