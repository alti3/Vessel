using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.Sources;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Sources;

/// <summary>DEP-08 / SEC-10: repository URL and git source boundary validation, host binding, provider URLs.</summary>
public sealed class GitSourceDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData("https://github.com/org/app.git", RepositoryTransport.Https, "github.com", "org/app.git")]
    [InlineData("git@github.com:org/app.git", RepositoryTransport.Ssh, "github.com", "org/app.git")]
    [InlineData("deploy@git.example.com:team/app.git", RepositoryTransport.Ssh, "git.example.com", "team/app.git")]
    [InlineData("ssh://git@git.example.com:2222/team/app.git", RepositoryTransport.Ssh, "git.example.com",
        "team/app.git")]
    [InlineData("http://gitea.internal/org/app", RepositoryTransport.Http, "gitea.internal", "org/app")]
    public void Repository_urls_of_supported_transports_parse(string value, RepositoryTransport transport,
        string host, string path)
    {
        var url = new RepositoryUrl(value);

        Assert.Equal(transport, url.Transport);
        Assert.Equal(host, url.Host);
        Assert.Equal(path, url.Path);
    }

    [Theory]
    [InlineData("-uhttps://github.com/org/app")]
    [InlineData("--upload-pack=touch /tmp/x")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/srv/repos/app.git")]
    [InlineData("https://github.com/org/app.git\nmalicious")]
    [InlineData("https://github.com/org/app .git")]
    [InlineData("git@-oProxyCommand=id:org/app.git")]
    [InlineData("-oProxyCommand=id@host:org/app.git")]
    [InlineData("ssh://-oProxyCommand=id/org/app.git")]
    [InlineData("https://github.com/org/app.git?x=1")]
    [InlineData("https://github.com/../etc")]
    [InlineData("ftp://example.com/repo.git")]
    public void Unsafe_repository_urls_are_rejected(string value)
    {
        Assert.Throws<DomainException>(() => new RepositoryUrl(value));
    }

    [Fact]
    public void Input_with_embedded_credentials_is_rejected()
    {
        Assert.True(new RepositoryUrl("https://user:token@github.com/org/app.git").HasEmbeddedPassword);
        Assert.Throws<DomainException>(() => RepositoryUrl.ParseInput("https://user:token@github.com/org/app.git"));
        Assert.Equal("github.com", RepositoryUrl.ParseInput("https://github.com/org/app.git").Host);
    }

    [Fact]
    public void Scp_urls_become_ssh_uris_for_git()
    {
        Uri uri = new RepositoryUrl("git@github.com:org/app.git").ToTransportUri();

        Assert.Equal("ssh://git@github.com/org/app.git", uri.ToString());
    }

    [Theory]
    [InlineData("-b")]
    [InlineData("main;id")]
    [InlineData("feature/../x")]
    [InlineData("--orphan")]
    public void Git_source_input_rejects_unsafe_branches(string branch)
    {
        Assert.Throws<DomainException>(() => GitSource.FromInput("https://github.com/org/app.git", branch));
    }

    [Fact]
    public void Git_source_input_rejects_unsafe_commits_and_defaults_branch()
    {
        Assert.Throws<DomainException>(() =>
            GitSource.FromInput("https://github.com/org/app.git", "main", "--output=/tmp/x"));
        Assert.Equal("main", GitSource.FromInput("https://github.com/org/app.git", null).Branch);
    }

    [Fact]
    public void Persisted_sources_are_revalidated_before_git()
    {
        var unsafeSource = new GitSource(new RepositoryUrl("https://github.com/org/app.git"), "--upload-pack=x",
            null);

        Assert.Throws<DomainException>(unsafeSource.EnsureSafeForGit);
    }

    [Theory]
    [InlineData("https://github.com", "https://api.github.com")]
    [InlineData("https://octo.ghe.com", "https://api.octo.ghe.com")]
    [InlineData("https://github.example.com/", "https://github.example.com/api/v3")]
    public void GitHub_api_url_is_derived_from_the_html_url(string html, string api)
    {
        Assert.Equal(api, GitProviderEndpoints.GitHubApiUrl(html));
    }

    [Fact]
    public void GitLab_and_gitea_api_urls_keep_the_path_prefix()
    {
        Assert.Equal("https://example.com/gitlab/api/v4",
            GitProviderEndpoints.ApiUrlFor(GitSourceKind.GitLabApp, "https://example.com/gitlab/"));
        Assert.Equal("https://codeberg.org/api/v1",
            GitProviderEndpoints.ApiUrlFor(GitSourceKind.GiteaApp, "https://codeberg.org"));
    }

    [Theory]
    [InlineData("http://github.example.com")]
    [InlineData("https://user:pass@github.example.com")]
    [InlineData("https://github.example.com/?q=1")]
    [InlineData("not a url")]
    public void Provider_urls_must_be_plain_https(string value)
    {
        Assert.Throws<DomainException>(() => GitProviderEndpoints.NormalizeHtmlUrl(value));
    }

    [Fact]
    public void Token_sources_are_bound_to_their_host_scheme_and_prefix()
    {
        GitSourceConnection source = HttpsSource("https://git.example.com/gitlab");

        Assert.Null(source.CheckRepository(new RepositoryUrl("https://git.example.com/gitlab/org/app.git")));
        Assert.NotNull(source.CheckRepository(new RepositoryUrl("https://evil.example.net/gitlab/org/app.git")));
        Assert.NotNull(source.CheckRepository(new RepositoryUrl("http://git.example.com/gitlab/org/app.git")));
        Assert.NotNull(source.CheckRepository(new RepositoryUrl("https://git.example.com:8443/gitlab/org/app.git")));
        Assert.NotNull(source.CheckRepository(new RepositoryUrl("https://git.example.com/other/org/app.git")));
        Assert.NotNull(source.CheckRepository(new RepositoryUrl("git@git.example.com:gitlab/org/app.git")));
    }

    [Fact]
    public void Organization_filter_limits_repository_owners()
    {
        GitSourceConnection source = GitSourceConnection.CreateGitHubApp(TeamId.New(), new ResourceName("gh"),
            "https://github.com", null, 1, 2, null, SecretReferenceId.New(), null, "acme, Tools", false, Now);

        Assert.Null(source.CheckRepository(new RepositoryUrl("https://github.com/tools/app.git")));
        Assert.NotNull(source.CheckRepository(new RepositoryUrl("https://github.com/other/app.git")));
        Assert.Equal("https://api.github.com", source.ApiUrl);
    }

    [Fact]
    public void Deploy_key_sources_require_ssh_urls()
    {
        GitSourceConnection source = GitSourceConnection.CreateDeployKey(TeamId.New(), new ResourceName("key"),
            SshKeyId.New(), null, null, null, Now);

        Assert.Null(source.CheckRepository(new RepositoryUrl("git@github.com:org/app.git")));
        Assert.NotNull(source.CheckRepository(new RepositoryUrl("https://github.com/org/app.git")));
        Assert.Throws<DomainException>(() => source.SetSystemWide(true, Now));
    }

    [Fact]
    public void Https_usernames_cannot_carry_a_password_separator()
    {
        Assert.Throws<DomainException>(() => GitSourceConnection.CreateHttpsCredential(TeamId.New(),
            new ResourceName("https"), "https://git.example.com", "user:pass", SecretReferenceId.New(), Now));
    }

    [Fact]
    public void Published_host_keys_cover_provider_ssh_endpoints()
    {
        Assert.Equal(3, GitHostKeys.PublishedFor("github.com", 22).Count);
        Assert.Equal(3, GitHostKeys.PublishedFor("ssh.github.com", 443).Count);
        Assert.Equal(3, GitHostKeys.PublishedFor("GitLab.com", 22).Count);
        Assert.Empty(GitHostKeys.PublishedFor("github.com", 2222));
        Assert.Empty(GitHostKeys.PublishedFor("git.example.com", 22));
        Assert.All(GitHostKeys.Published, line => Assert.True(GitHostKeys.IsValidLine(line)));
    }

    [Theory]
    [InlineData("@cert-authority * ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl")]
    [InlineData("|1|abc=|def= ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl")]
    [InlineData("host ssh-dss AAAAB3NzaC1kc3MAAACBAP")]
    [InlineData("host ssh-ed25519 not-base64!")]
    public void Known_hosts_lines_with_markers_hashes_or_weak_keys_are_rejected(string line)
    {
        Assert.False(GitHostKeys.IsValidLine(line));
    }

    [Fact]
    public void Known_host_pins_use_bracketed_ports()
    {
        GitKnownHost pin = GitKnownHost.Create(TeamId.New(), "Git.Example.com", 2222, "ssh-ed25519",
            "AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl", GitKnownHostOrigin.FirstUse, Now);

        Assert.StartsWith("[git.example.com]:2222 ssh-ed25519 ", pin.KnownHostsLine, StringComparison.Ordinal);
    }

    internal static GitSourceConnection HttpsSource(string htmlUrl, TeamId? teamId = null)
    {
        return GitSourceConnection.CreateHttpsCredential(teamId ?? TeamId.New(), new ResourceName("https"), htmlUrl,
            "deploy", SecretReferenceId.New(), Now);
    }
}
