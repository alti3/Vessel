using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.UnitTests.Domain;

public sealed class GitRefAndRepositoryIdentityTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("feature/login-v2")]
    [InlineData("v1.2.3")]
    [InlineData("refs/pull/7/head")]
    [InlineData("refs/merge-requests/7/head")]
    [InlineData("HEAD")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public void GitRef_accepts_branches_tags_shas_and_provider_refs(string value)
    {
        Assert.Equal(value, GitRef.Parse(value).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-b")]
    [InlineData("--upload-pack=touch /tmp/x")]
    [InlineData("main branch")]
    [InlineData("a..b")]
    [InlineData("a//b")]
    [InlineData("branch.lock")]
    [InlineData("branch/")]
    [InlineData("main@{1}")]
    [InlineData("main;id")]
    [InlineData(".hidden")]
    [InlineData("refs/.hidden")]
    public void GitRef_rejects_option_injection_and_invalid_names(string value)
    {
        Assert.False(GitRef.TryParse(value, out _));
        Assert.Throws<DomainException>(() => GitRef.Parse(value));
    }

    [Fact]
    public void GitRef_classifies_commit_shas()
    {
        Assert.True(GitRef.Parse("abc1234").IsCommitSha);
        Assert.False(GitRef.Parse("abc1234").IsFullCommitSha);
        Assert.True(GitRef.Parse(new string('a', 40)).IsFullCommitSha);
        Assert.True(GitRef.Parse(new string('b', 64)).IsFullCommitSha);
        Assert.False(GitRef.Parse("main").IsCommitSha);
        Assert.True(GitRef.IsZeroSha(new string('0', 40)));
        Assert.False(GitRef.IsZeroSha("0123456"));
    }

    [Theory]
    [InlineData("https://github.com/Org/App.git")]
    [InlineData("https://token@github.com/org/app")]
    [InlineData("git@github.com:org/app.git")]
    [InlineData("https://github.com:443/org/app/")]
    public void RepositoryIdentity_normalizes_url_forms(string url)
    {
        RepositoryIdentity? identity = RepositoryIdentity.FromRepositoryUrl(url);

        Assert.NotNull(identity);
        Assert.Equal("github.com", identity.Value.Host);
        Assert.Equal("org/app", identity.Value.Path);
        Assert.True(identity.Value.Matches(RepositoryIdentity.FromFullName("org/app", "https://github.com/org/app")!
            .Value));
    }

    [Fact]
    public void RepositoryIdentity_matches_exactly_never_by_substring()
    {
        RepositoryIdentity app = RepositoryIdentity.FromRepositoryUrl("https://github.com/org/app-private.git")!.Value;
        RepositoryIdentity other = RepositoryIdentity.FromRepositoryUrl("https://github.com/org/app.git")!.Value;
        RepositoryIdentity webhook = RepositoryIdentity.FromFullName("org/app")!.Value;

        Assert.False(app.Matches(webhook));
        Assert.True(other.Matches(webhook));
        Assert.False(RepositoryIdentity.FromFullName("other/org/app")!.Value.Matches(webhook));
    }

    [Fact]
    public void RepositoryIdentity_requires_matching_hosts_when_both_are_known()
    {
        RepositoryIdentity gitlab = RepositoryIdentity.FromRepositoryUrl("https://gitlab.example.com/org/app.git")!
            .Value;

        Assert.False(gitlab.Matches(RepositoryIdentity.FromFullName("org/app", "https://github.com/org/app")!.Value));
        Assert.True(gitlab.Matches(RepositoryIdentity.FromFullName("org/app")!.Value));
        Assert.Null(RepositoryIdentity.FromFullName("app"));
    }
}
