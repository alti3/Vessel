using System.Text.Json;
using Vessel.Domain.Webhooks;

namespace Vessel.Application.Webhooks;

public static class WebhookPayloadReader
{
    public static string EventType(WebhookProvider provider, IReadOnlyDictionary<string, string> headers,
        JsonElement payload)
    {
        headers = EnvelopeHeaders(headers, payload);
        payload = PayloadRoot(payload);
        return provider switch
        {
            WebhookProvider.GitHub => Header(headers, "X-GitHub-Event") ?? "unknown",
            WebhookProvider.Gitea => Header(headers, "X-Gitea-Event") ?? "unknown",
            WebhookProvider.GitLab => Text(payload, "object_kind") ?? Header(headers, "X-Gitlab-Event") ?? "unknown",
            WebhookProvider.Bitbucket => Header(headers, "X-Event-Key") ?? "unknown",
            WebhookProvider.Generic => "deploy",
            _ => "unknown"
        };
    }

    public static string? ProviderEventId(WebhookProvider provider, IReadOnlyDictionary<string, string> headers,
        JsonElement payload)
    {
        headers = EnvelopeHeaders(headers, payload);
        payload = PayloadRoot(payload);
        return provider switch
        {
            WebhookProvider.GitHub => Header(headers, "X-GitHub-Delivery"),
            WebhookProvider.Gitea => Header(headers, "X-Gitea-Delivery"),
            WebhookProvider.GitLab => Text(payload, "event_id") ??
                                      Text(payload, "checkout_sha") ?? Text(payload, "after"),
            WebhookProvider.Bitbucket => Header(headers, "X-Request-UUID") ??
                                         Text(payload, "push.changes.0.new.target.hash") ??
                                         Text(payload, "pullrequest.id"),
            WebhookProvider.Generic => Text(payload, "eventId"),
            _ => null
        };
    }

    public static ParsedWebhook? Parse(WebhookProvider provider, IReadOnlyDictionary<string, string> headers,
        JsonElement payload)
    {
        headers = EnvelopeHeaders(headers, payload);
        payload = PayloadRoot(payload);
        var eventType = EventType(provider, headers, payload);
        return provider switch
        {
            WebhookProvider.GitHub or WebhookProvider.Gitea => ParseGithubLike(provider, eventType, payload),
            WebhookProvider.GitLab => ParseGitLab(eventType, payload),
            WebhookProvider.Bitbucket => ParseBitbucket(eventType, payload),
            WebhookProvider.Generic => ParseGeneric(payload),
            _ => null
        };
    }

    public static string? Header(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.TryGetValue(name, out var value)
            ? value
            : headers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static JsonElement PayloadRoot(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("payload", out JsonElement payload)
            ? payload
            : element;
    }

    public static string RawBody(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("rawBody", out JsonElement rawBody)
            ? rawBody.GetString() ?? PayloadRoot(element).GetRawText()
            : element.GetRawText();
    }

    public static IReadOnlyDictionary<string, string> EnvelopeHeaders(IReadOnlyDictionary<string, string> headers,
        JsonElement element)
    {
        if (headers.Count > 0 ||
            element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("headers", out JsonElement headerElement) ||
            headerElement.ValueKind != JsonValueKind.Object)
            return headers;

        return headerElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    public static string? Text(JsonElement element, string path)
    {
        JsonElement current = element;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index < 0 || index >= current.GetArrayLength()) return null;
                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static ParsedWebhook? ParseGithubLike(WebhookProvider provider, string eventType, JsonElement payload)
    {
        if (string.Equals(eventType, "push", StringComparison.OrdinalIgnoreCase))
        {
            var branch = NormalizeRef(Text(payload, "ref"));
            var repo = Text(payload, "repository.full_name");
            var commit = Text(payload, "after");
            return branch is null || repo is null
                ? null
                : ParsedWebhook.Push(provider, repo, branch, commit ?? "HEAD", CommitMessages(payload, "commits"),
                    ChangedFiles(payload, "commits")) with
                {
                    RepositoryWebUrl = Text(payload, "repository.html_url") ?? Text(payload, "repository.clone_url")
                };
        }

        if (string.Equals(eventType, "pull_request", StringComparison.OrdinalIgnoreCase))
        {
            var action = Text(payload, "action");
            var repo = Text(payload, "repository.full_name");
            var source = Text(payload, "pull_request.head.ref");
            var target = Text(payload, "pull_request.base.ref");
            var number = Text(payload, "number");
            var commit = Text(payload, "pull_request.head.sha") ?? Text(payload, "after") ?? "HEAD";
            return repo is null || source is null || target is null || !int.TryParse(number, out var pr)
                ? null
                : ParsedWebhook.PullRequest(provider, repo, action ?? "unknown", pr, source, target, commit,
                    Text(payload, "pull_request.html_url"), Text(payload, "pull_request.title")) with
                {
                    RepositoryWebUrl = Text(payload, "repository.html_url") ?? Text(payload, "repository.clone_url"),
                    SourceRepository = Text(payload, "pull_request.head.repo.full_name"),
                    TargetRepository = Text(payload, "pull_request.base.repo.full_name")
                };
        }

        return null;
    }

    private static ParsedWebhook? ParseGitLab(string eventType, JsonElement payload)
    {
        if (string.Equals(eventType, "push", StringComparison.OrdinalIgnoreCase))
        {
            var branch = NormalizeRef(Text(payload, "ref"));
            var repo = Text(payload, "project.path_with_namespace");
            var commit = Text(payload, "after");
            return branch is null || repo is null
                ? null
                : ParsedWebhook.Push(WebhookProvider.GitLab, repo, branch, commit ?? "HEAD",
                    CommitMessages(payload, "commits"), ChangedFiles(payload, "commits")) with
                {
                    RepositoryWebUrl = Text(payload, "project.web_url")
                };
        }

        if (string.Equals(eventType, "merge_request", StringComparison.OrdinalIgnoreCase))
        {
            var repo = Text(payload, "project.path_with_namespace");
            var source = Text(payload, "object_attributes.source_branch");
            var target = Text(payload, "object_attributes.target_branch");
            var number = Text(payload, "object_attributes.iid");
            var commit = Text(payload, "object_attributes.last_commit.id") ?? "HEAD";
            return repo is null || source is null || target is null || !int.TryParse(number, out var pr)
                ? null
                : ParsedWebhook.PullRequest(WebhookProvider.GitLab, repo,
                    Text(payload, "object_attributes.action") ?? "unknown", pr, source, target, commit,
                    Text(payload, "object_attributes.url"), Text(payload, "object_attributes.title")) with
                {
                    RepositoryWebUrl = Text(payload, "project.web_url"),
                    SourceRepository = Text(payload, "object_attributes.source.path_with_namespace"),
                    TargetRepository = Text(payload, "object_attributes.target.path_with_namespace")
                };
        }

        return null;
    }

    private static ParsedWebhook? ParseBitbucket(string eventType, JsonElement payload)
    {
        if (string.Equals(eventType, "repo:push", StringComparison.OrdinalIgnoreCase))
        {
            var branch = Text(payload, "push.changes.0.new.name");
            var repo = Text(payload, "repository.full_name");
            var commit = Text(payload, "push.changes.0.new.target.hash");
            return branch is null || repo is null
                ? null
                : ParsedWebhook.Push(WebhookProvider.Bitbucket, repo, branch, commit ?? "HEAD",
                    CommitMessages(payload, "push.changes.0.commits"), []) with
                {
                    RepositoryWebUrl = Text(payload, "repository.links.html.href")
                };
        }

        if (eventType.StartsWith("pullrequest:", StringComparison.OrdinalIgnoreCase))
        {
            var repo = Text(payload, "repository.full_name");
            var source = Text(payload, "pullrequest.source.branch.name");
            var target = Text(payload, "pullrequest.destination.branch.name");
            var number = Text(payload, "pullrequest.id");
            var commit = Text(payload, "pullrequest.source.commit.hash") ?? "HEAD";
            return repo is null || source is null || target is null || !int.TryParse(number, out var pr)
                ? null
                : ParsedWebhook.PullRequest(WebhookProvider.Bitbucket, repo, eventType, pr, source, target, commit,
                    Text(payload, "pullrequest.links.html.href"), Text(payload, "pullrequest.title")) with
                {
                    RepositoryWebUrl = Text(payload, "repository.links.html.href"),
                    SourceRepository = Text(payload, "pullrequest.source.repository.full_name"),
                    TargetRepository = Text(payload, "pullrequest.destination.repository.full_name")
                };
        }

        return null;
    }

    private static ParsedWebhook? ParseGeneric(JsonElement payload)
    {
        var applicationId = Text(payload, "applicationId");
        var commit = Text(payload, "commitSha") ?? Text(payload, "commit") ?? "HEAD";
        return Guid.TryParse(applicationId, out Guid parsed)
            ? ParsedWebhook.GenericDeploy(parsed, commit)
            : null;
    }

    private static string? NormalizeRef(string? value)
    {
        return value?.StartsWith("refs/heads/", StringComparison.Ordinal) == true
            ? value["refs/heads/".Length..]
            : value;
    }

    private static IReadOnlyList<string> CommitMessages(JsonElement payload, string path)
    {
        JsonElement? commits = ArrayElement(payload, path);
        return commits.HasValue
            ? commits.Value.EnumerateArray().Select(item => Text(item, "message")).Where(item => item is not null)
                .Cast<string>().ToArray()
            : [];
    }

    private static IReadOnlyList<string> ChangedFiles(JsonElement payload, string path)
    {
        JsonElement? commits = ArrayElement(payload, path);
        if (!commits.HasValue) return [];
        return NetChangedFiles(commits.Value.EnumerateArray()
            .Select(commit => (Files(commit, "added"), Files(commit, "modified"), Files(commit, "removed"))));
    }

    /// <summary>
    ///     The push's net file change (Coolify v4.5.0 uses the net change of a push for watch paths): a file added and
    ///     removed again within the same push is not a change; any other touched file is.
    /// </summary>
    public static IReadOnlyList<string> NetChangedFiles(
        IEnumerable<(IReadOnlyList<string> Added, IReadOnlyList<string> Modified, IReadOnlyList<string> Removed)> commits)
    {
        var state = new Dictionary<string, char>(StringComparer.Ordinal);
        var order = new List<string>();
        void Set(string file, char value)
        {
            if (!state.ContainsKey(file)) order.Add(file);
            state[file] = value;
        }

        foreach (var (added, modified, removed) in commits)
        {
            foreach (var file in added)
                Set(file, state.TryGetValue(file, out var previous) && previous == 'R' ? 'M' : 'A');
            foreach (var file in modified)
                Set(file, state.TryGetValue(file, out var previous) && previous == 'A' ? 'A' : 'M');
            foreach (var file in removed)
                if (state.TryGetValue(file, out var previous) && previous == 'A') state[file] = 'N';
                else Set(file, 'R');
        }

        return order.Where(file => state[file] != 'N').ToArray();
    }

    private static IReadOnlyList<string> Files(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement files) && files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray().Select(file => file.GetString()).Where(file => file is not null).Cast<string>()
                .ToArray()
            : [];
    }

    private static JsonElement? ArrayElement(JsonElement payload, string path)
    {
        JsonElement current = payload;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index < 0 || index >= current.GetArrayLength()) return null;
                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.Array ? current : null;
    }
}

public sealed record ParsedWebhook(
    WebhookProvider Provider,
    string Kind,
    string? Repository,
    string? Branch,
    string? CommitSha,
    string? Action,
    int? PullRequestNumber,
    string? SourceBranch,
    string? TargetBranch,
    string? PullRequestUrl,
    string? PullRequestTitle,
    Guid? ApplicationId,
    IReadOnlyList<string> CommitMessages,
    IReadOnlyList<string> ChangedFiles)
{
    /// <summary>Web or clone URL of the webhook repository, used for host-aware repository matching.</summary>
    public string? RepositoryWebUrl { get; init; }

    /// <summary>Full name of the pull request head (source) repository; differs from the target for forks.</summary>
    public string? SourceRepository { get; init; }

    /// <summary>Full name of the pull request base (target) repository.</summary>
    public string? TargetRepository { get; init; }

    /// <summary>
    ///     Coolify <c>isPreviewDeploymentRepositoryTrusted</c>: the target must be the webhook repository, and the source
    ///     must equal the target unless public (fork) previews are enabled. Missing identities are never trusted.
    /// </summary>
    public bool IsPreviewRepositoryTrusted(bool publicPreviewsEnabled)
    {
        var source = SourceRepository?.Trim();
        var target = TargetRepository?.Trim();
        var webhook = Repository?.Trim();
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || string.IsNullOrEmpty(webhook))
            return false;
        if (!string.Equals(target, webhook, StringComparison.Ordinal)) return false;
        return string.Equals(source, target, StringComparison.Ordinal) || publicPreviewsEnabled;
    }

    /// <summary>True only when the pull request branch lives in the target repository itself.</summary>
    public bool IsSameRepositoryPullRequest =>
        !string.IsNullOrWhiteSpace(TargetRepository) && !string.IsNullOrWhiteSpace(SourceRepository) &&
        string.Equals(TargetRepository.Trim(), SourceRepository.Trim(), StringComparison.Ordinal);

    public static ParsedWebhook Push(WebhookProvider provider, string repository, string branch, string commitSha,
        IReadOnlyList<string> messages, IReadOnlyList<string> changedFiles)
    {
        return new ParsedWebhook(provider, "push", repository, branch, commitSha, null, null, null, null, null, null,
            null, messages,
            changedFiles);
    }

    public static ParsedWebhook PullRequest(WebhookProvider provider, string repository, string action, int number,
        string sourceBranch, string targetBranch, string commitSha, string? url, string? title)
    {
        return new ParsedWebhook(provider, "pull_request", repository, targetBranch, commitSha, action, number,
            sourceBranch,
            targetBranch, url, title, null, title is null ? [] : [title], []);
    }

    public static ParsedWebhook GenericDeploy(Guid applicationId, string commitSha)
    {
        return new ParsedWebhook(WebhookProvider.Generic, "generic", null, null, commitSha, null, null, null, null,
            null, null,
            applicationId, [], []);
    }
}
