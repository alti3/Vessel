using System.Text.Json;
using Vessel.Application.Webhooks;
using Vessel.Domain.Webhooks;

namespace Vessel.UnitTests.Webhooks;

public sealed class WebhookPayloadReaderTests
{
    [Fact]
    public void Parse_GitHubPushExtractsBranchRepositoryCommitAndChangedFiles()
    {
        using JsonDocument document = JsonDocument.Parse("""
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
        using JsonDocument document = JsonDocument.Parse("""
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

        ParsedWebhook parsed = WebhookPayloadReader.Parse(WebhookProvider.GitLab, new Dictionary<string, string>(), document.RootElement)!;

        Assert.Equal("pull_request", parsed.Kind);
        Assert.Equal(7, parsed.PullRequestNumber);
        Assert.Equal("feature", parsed.SourceBranch);
        Assert.Equal("main", parsed.TargetBranch);
        Assert.Equal("def456", parsed.CommitSha);
    }
}
