using System.Text.Json;
using Vessel.Application.Webhooks;
using Vessel.Domain.Webhooks;

namespace Vessel.UnitTests.Webhooks;

public sealed class WebhookPayloadReaderTests
{
    [Fact]
    public void Parse_GitHubPushExtractsBranchRepositoryCommitAndChangedFiles()
    {
        using var document = JsonDocument.Parse("""
                                                {
                                                  "ref": "refs/heads/main",
                                                  "after": "abc123",
                                                  "repository": { "full_name": "owner/repo" },
                                                  "commits": [
                                                    { "message": "Update", "added": ["src/a.cs"], "modified": ["README.md"], "removed": [] }
                                                  ]
                                                }
                                                """);

        ParsedWebhook parsed = WebhookPayloadReader.Parse(
            WebhookProvider.GitHub,
            new Dictionary<string, string> { ["X-GitHub-Event"] = "push" },
            document.RootElement)!;

        Assert.Equal("push", parsed.Kind);
        Assert.Equal("owner/repo", parsed.Repository);
        Assert.Equal("main", parsed.Branch);
        Assert.Equal("abc123", parsed.CommitSha);
        Assert.Contains("src/a.cs", parsed.ChangedFiles);
    }

    [Fact]
    public void Parse_GitLabMergeRequestExtractsPreviewFields()
    {
        using var document = JsonDocument.Parse("""
                                                {
                                                  "object_kind": "merge_request",
                                                  "project": { "path_with_namespace": "group/repo" },
                                                  "object_attributes": {
                                                    "action": "open",
                                                    "iid": 7,
                                                    "source_branch": "feature",
                                                    "target_branch": "main",
                                                    "url": "https://gitlab.example/group/repo/-/merge_requests/7",
                                                    "title": "Feature",
                                                    "last_commit": { "id": "def456" }
                                                  }
                                                }
                                                """);

        ParsedWebhook parsed = WebhookPayloadReader.Parse(WebhookProvider.GitLab, new Dictionary<string, string>(),
            document.RootElement)!;

        Assert.Equal("pull_request", parsed.Kind);
        Assert.Equal(7, parsed.PullRequestNumber);
        Assert.Equal("feature", parsed.SourceBranch);
        Assert.Equal("main", parsed.TargetBranch);
        Assert.Equal("def456", parsed.CommitSha);
    }

    [Fact]
    public void Parse_GitHubForkPullRequestExposesSourceAndTargetRepositories()
    {
        using var document = JsonDocument.Parse("""
                                                {
                                                  "action": "opened", "number": 3,
                                                  "repository": { "full_name": "owner/repo", "html_url": "https://github.com/owner/repo" },
                                                  "pull_request": {
                                                    "head": { "ref": "patch", "sha": "abc1234", "repo": { "full_name": "fork/repo" } },
                                                    "base": { "ref": "main", "repo": { "full_name": "owner/repo" } }
                                                  }
                                                }
                                                """);

        ParsedWebhook parsed = WebhookPayloadReader.Parse(WebhookProvider.GitHub,
            new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" }, document.RootElement)!;

        Assert.Equal("fork/repo", parsed.SourceRepository);
        Assert.Equal("owner/repo", parsed.TargetRepository);
        Assert.Equal("https://github.com/owner/repo", parsed.RepositoryWebUrl);
        Assert.False(parsed.IsSameRepositoryPullRequest);
        Assert.False(parsed.IsPreviewRepositoryTrusted(false));
        Assert.True(parsed.IsPreviewRepositoryTrusted(true));
    }

    [Fact]
    public void Parse_GitLabAndBitbucketPullRequestsExposeRepositoryIdentities()
    {
        using var gitlab = JsonDocument.Parse("""
                                              {
                                                "object_kind": "merge_request",
                                                "project": { "path_with_namespace": "group/repo", "web_url": "https://gitlab.example/group/repo" },
                                                "object_attributes": {
                                                  "action": "open", "iid": 7, "source_branch": "feature", "target_branch": "main",
                                                  "source": { "path_with_namespace": "group/repo" },
                                                  "target": { "path_with_namespace": "group/repo" },
                                                  "last_commit": { "id": "def4567" }
                                                }
                                              }
                                              """);
        using var bitbucket = JsonDocument.Parse("""
                                                 {
                                                   "repository": { "full_name": "team/repo" },
                                                   "pullrequest": {
                                                     "id": 4,
                                                     "source": { "branch": { "name": "feature" }, "commit": { "hash": "abc1234" }, "repository": { "full_name": "someone/repo" } },
                                                     "destination": { "branch": { "name": "main" }, "repository": { "full_name": "team/repo" } }
                                                   }
                                                 }
                                                 """);

        ParsedWebhook gitlabParsed = WebhookPayloadReader.Parse(WebhookProvider.GitLab,
            new Dictionary<string, string>(), gitlab.RootElement)!;
        ParsedWebhook bitbucketParsed = WebhookPayloadReader.Parse(WebhookProvider.Bitbucket,
            new Dictionary<string, string> { ["X-Event-Key"] = "pullrequest:created" }, bitbucket.RootElement)!;

        Assert.True(gitlabParsed.IsSameRepositoryPullRequest);
        Assert.True(gitlabParsed.IsPreviewRepositoryTrusted(false));
        Assert.Equal("someone/repo", bitbucketParsed.SourceRepository);
        Assert.False(bitbucketParsed.IsPreviewRepositoryTrusted(false));
    }

    [Fact]
    public void PullRequestWithoutRepositoryIdentitiesIsNeverTrusted()
    {
        ParsedWebhook parsed = ParsedWebhook.PullRequest(WebhookProvider.GitHub, "owner/repo", "opened", 1,
            "feature", "main", "abc1234", null, null);

        Assert.False(parsed.IsPreviewRepositoryTrusted(true));
        Assert.False(parsed.IsSameRepositoryPullRequest);
    }

    [Fact]
    public void PullRequestTargetMustBeTheWebhookRepository()
    {
        ParsedWebhook parsed = ParsedWebhook.PullRequest(WebhookProvider.GitHub, "owner/repo", "opened", 1,
            "feature", "main", "abc1234", null, null) with
        {
            SourceRepository = "other/repo",
            TargetRepository = "other/repo"
        };

        Assert.False(parsed.IsPreviewRepositoryTrusted(true));
    }
}
