using Vessel.Application.Files;
using Vessel.Application.Git;
using Vessel.Application.Processes;

namespace Vessel.Infrastructure.Git;

public sealed class GitProcessClient(IProcessRunner processRunner, IPathSafetyService paths) : IGitClient
{
    public async Task CloneAsync(GitCloneRequest request, CancellationToken cancellationToken = default)
    {
        string target = paths.EnsureOwnedPath(Path.GetDirectoryName(Path.GetFullPath(request.TargetDirectory))!, request.TargetDirectory);
        var args = new List<string> { "clone", "--depth", request.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(request.BranchOrTag))
            args.AddRange(["--branch", request.BranchOrTag]);
        if (request.RecurseSubmodules)
            args.AddRange(["--recurse-submodules", "--shallow-submodules"]);
        args.AddRange([request.RepositoryUrl.ToString(), target]);

        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "git",
            args,
            Environment: request.Environment,
            Timeout: TimeSpan.FromMinutes(30),
            Redaction: new ProcessRedactionProfile([request.RepositoryUrl.ToString()], [])), cancellationToken);
        ThrowIfFailed(result);
    }

    public async Task FetchAsync(string repositoryDirectory, string? remote, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "fetch", "--prune" };
        if (!string.IsNullOrWhiteSpace(remote)) args.Add(remote);
        ProcessResult result = await processRunner.RunTextAsync(Command(repositoryDirectory, args), cancellationToken);
        ThrowIfFailed(result);
    }

    public async Task CheckoutAsync(string repositoryDirectory, string reference, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(Command(repositoryDirectory, ["checkout", "--force", reference]), cancellationToken);
        ThrowIfFailed(result);
    }

    public async Task<GitCommitInfo> GetCommitAsync(
        string repositoryDirectory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(Command(
            repositoryDirectory,
            ["show", "-s", "--format=%H%x00%an%x00%ae%x00%aI%x00%s", reference]), cancellationToken);
        ThrowIfFailed(result);
        string[] parts = result.StandardOutput.TrimEnd().Split('\0');
        return new GitCommitInfo(
            parts.ElementAtOrDefault(0) ?? string.Empty,
            parts.ElementAtOrDefault(1) ?? string.Empty,
            parts.ElementAtOrDefault(2) ?? string.Empty,
            DateTimeOffset.TryParse(parts.ElementAtOrDefault(3), out DateTimeOffset authoredAt) ? authoredAt : DateTimeOffset.MinValue,
            parts.ElementAtOrDefault(4) ?? string.Empty);
    }

    public async Task<IReadOnlyList<GitRepositoryRef>> ListRefsAsync(Uri repositoryUrl, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunTextAsync(new ProcessCommand(
            "git",
            ["ls-remote", "--heads", "--tags", repositoryUrl.ToString()],
            Timeout: TimeSpan.FromMinutes(2),
            Redaction: new ProcessRedactionProfile([repositoryUrl.ToString()], [])), cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseRef)
            .ToArray();
    }

    private static ProcessCommand Command(string repositoryDirectory, IReadOnlyList<string> args) =>
        new("git", args, WorkingDirectory: repositoryDirectory, Timeout: TimeSpan.FromMinutes(5));

    private static GitRepositoryRef ParseRef(string line)
    {
        string[] parts = line.Split('\t', 2);
        string name = parts.ElementAtOrDefault(1) ?? string.Empty;
        bool isTag = name.StartsWith("refs/tags/", StringComparison.Ordinal);
        name = name.Replace("refs/heads/", string.Empty, StringComparison.Ordinal)
            .Replace("refs/tags/", string.Empty, StringComparison.Ordinal);
        return new GitRepositoryRef(name, parts.ElementAtOrDefault(0) ?? string.Empty, isTag);
    }

    private static void ThrowIfFailed(ProcessResult result)
    {
        if (!result.Succeeded) throw new ProcessExecutionException(result);
    }
}
