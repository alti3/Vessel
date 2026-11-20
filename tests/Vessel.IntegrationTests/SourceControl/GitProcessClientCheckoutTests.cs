using Vessel.Application.Git;
using Vessel.Application.Processes;
using Vessel.Infrastructure.Files;
using Vessel.Infrastructure.Git;
using Vessel.Infrastructure.Processes;
using Vessel.Infrastructure.Security;

namespace Vessel.IntegrationTests.SourceControl;

/// <summary>DEP-01: checkouts of branches, exact commits (full and abbreviated) and pull request refs.</summary>
public sealed class GitProcessClientCheckoutTests : IAsyncLifetime
{
    private readonly DotNetProcessRunner _runner = new(new SecretRedactor(), TimeProvider.System);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vessel-git-" + Guid.NewGuid().ToString("N"));
    private string _first = string.Empty;
    private string _pullHead = string.Empty;
    private string _second = string.Empty;
    private Uri _remote = null!;

    public async ValueTask InitializeAsync()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await RunAsync(source, "init", "--quiet", "--initial-branch=main");
        await CommitAsync(source, "first.txt", "first");
        _first = await RevParseAsync(source, "HEAD");
        await CommitAsync(source, "second.txt", "second");
        _second = await RevParseAsync(source, "HEAD");
        await RunAsync(source, "tag", "v1.0.0", _first);
        await RunAsync(source, "checkout", "--quiet", "-b", "feature");
        await CommitAsync(source, "feature.txt", "feature");
        _pullHead = await RevParseAsync(source, "HEAD");
        // Simulate a provider pull request ref that is not a branch (fork commits live only here).
        await RunAsync(source, "update-ref", "refs/pull/5/head", _pullHead);
        await RunAsync(source, "checkout", "--quiet", "main");
        await RunAsync(source, "branch", "--quiet", "-D", "feature");

        var bare = Path.Combine(_root, "remote.git");
        await RunAsync(_root, "clone", "--quiet", "--bare", source, bare);
        await RunAsync(bare, "update-ref", "refs/pull/5/head", _pullHead);
        _remote = new Uri(bare);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Checkout_fetches_the_branch_head()
    {
        var target = await CheckoutAsync(new GitCheckoutRequest(_remote, Target("branch"), GitCheckoutKind.Named,
            "main"));

        Assert.Equal(_second, await RevParseAsync(target, "HEAD"));
    }

    [Fact]
    public async Task Checkout_fetches_an_exact_older_commit()
    {
        var target = await CheckoutAsync(new GitCheckoutRequest(_remote, Target("commit"), GitCheckoutKind.Commit,
            _first, "main"));

        Assert.Equal(_first, await RevParseAsync(target, "HEAD"));
        Assert.False(File.Exists(Path.Combine(target, "second.txt")));
    }

    [Fact]
    public async Task Checkout_resolves_an_abbreviated_commit_from_the_branch()
    {
        var target = await CheckoutAsync(new GitCheckoutRequest(_remote, Target("short"), GitCheckoutKind.Commit,
            _first[..10], "main"));

        Assert.Equal(_first, await RevParseAsync(target, "HEAD"));
    }

    [Fact]
    public async Task Checkout_fetches_a_tag()
    {
        var target = await CheckoutAsync(new GitCheckoutRequest(_remote, Target("tag"), GitCheckoutKind.Named,
            "v1.0.0"));

        Assert.Equal(_first, await RevParseAsync(target, "HEAD"));
    }

    [Fact]
    public async Task Checkout_fetches_a_pull_request_ref_that_is_not_a_branch()
    {
        var target = await CheckoutAsync(new GitCheckoutRequest(_remote, Target("pull"),
            GitCheckoutKind.PullRequest, "refs/pull/5/head", null, _pullHead));

        Assert.Equal(_pullHead, await RevParseAsync(target, "HEAD"));
        Assert.True(File.Exists(Path.Combine(target, "feature.txt")));
    }

    [Theory]
    [InlineData("--upload-pack=touch /tmp/vessel-pwned")]
    [InlineData("-b")]
    [InlineData("main;id")]
    public async Task Checkout_rejects_option_like_refs_before_running_git(string reference)
    {
        var client = new GitProcessClient(_runner, new PathSafetyService());

        await Assert.ThrowsAnyAsync<Exception>(() => client.CheckoutAsync(
            new GitCheckoutRequest(_remote, Target("bad"), GitCheckoutKind.Named, reference),
            TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Target("bad")));
    }

    [Fact]
    public void Fetch_arguments_are_shallow_and_never_prompt()
    {
        Assert.Equal(["fetch", "--no-tags", "--no-recurse-submodules", "--depth", "1", "origin", "refs/pull/5/head"],
            GitProcessClient.FetchArguments("refs/pull/5/head", 1));
        IReadOnlyDictionary<string, string?> environment =
            GitProcessClient.NonInteractiveEnvironment(new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "1" });
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Contains("BatchMode=yes", environment["GIT_SSH_COMMAND"], StringComparison.Ordinal);
    }

    private string Target(string name)
    {
        return Path.Combine(_root, "checkouts", name);
    }

    private async Task<string> CheckoutAsync(GitCheckoutRequest request)
    {
        var client = new GitProcessClient(_runner, new PathSafetyService());
        await client.CheckoutAsync(request, TestContext.Current.CancellationToken);
        return request.TargetDirectory;
    }

    private async Task CommitAsync(string directory, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, file), content, TestContext.Current.CancellationToken);
        await RunAsync(directory, "add", file);
        await RunAsync(directory, "-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "--quiet",
            "-m", content);
    }

    private async Task<string> RevParseAsync(string directory, string revision)
    {
        return (await RunAsync(directory, "rev-parse", revision)).Trim();
    }

    private async Task<string> RunAsync(string directory, params string[] args)
    {
        ProcessResult result = await _runner.RunTextAsync(
            new ProcessCommand("git", args, directory, Timeout: TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput;
    }
}
