using System.Text.Json;
using Vessel.Domain.Webhooks;

namespace Vessel.Application.Webhooks;

public static class WebhookPayloadReader
{
    public static string EventType(WebhookProvider provider, IReadOnlyDictionary<string, string> headers, JsonElement payload)
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

    public static string? ProviderEventId(WebhookProvider provider, IReadOnlyDictionary<string, string> headers, JsonElement payload)
    {
        headers = EnvelopeHeaders(headers, payload);
        payload = PayloadRoot(payload);
        return provider switch
        {
            WebhookProvider.GitHub => Header(headers, "X-GitHub-Delivery"),
            WebhookProvider.Gitea => Header(headers, "X-Gitea-Delivery"),
            WebhookProvider.GitLab => Text(payload, "event_id") ?? Text(payload, "checkout_sha") ?? Text(payload, "after"),
            WebhookProvider.Bitbucket => Header(headers, "X-Request-UUID") ?? Text(payload, "push.changes.0.new.target.hash") ?? Text(payload, "pullrequest.id"),
            WebhookProvider.Generic => Text(payload, "eventId"),
            _ => null
        };
    }

    public static ParsedWebhook? Parse(WebhookProvider provider, IReadOnlyDictionary<string, string> headers, JsonElement payload)
    {
        headers = EnvelopeHeaders(headers, payload);
        payload = PayloadRoot(payload);
        string eventType = EventType(provider, headers, payload);
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
        return headers.TryGetValue(name, out string? value)
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

    public static IReadOnlyDictionary<string, string> EnvelopeHeaders(IReadOnlyDictionary<string, string> headers, JsonElement element)
    {
        if (headers.Count > 0 ||
            element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("headers", out JsonElement headerElement) ||
            headerElement.ValueKind != JsonValueKind.Object)
            return headers;

        return headerElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    public static string? Text(JsonElement element, string path)
    {
        JsonElement current = element;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out int index))
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
            string? branch = NormalizeRef(Text(payload, "ref"));
            string? repo = Text(payload, "repository.full_name");
            string? commit = Text(payload, "after");
            return branch is null || repo is null
                ? null
                : ParsedWebhook.Push(provider, repo, branch, commit ?? "HEAD", CommitMessages(payload, "commits"), ChangedFiles(payload, "commits"));
        }

        if (string.Equals(eventType, "pull_request", StringComparison.OrdinalIgnoreCase))
        {
            string? action = Text(payload, "action");
            string? repo = Text(payload, "repository.full_name");
            string? source = Text(payload, "pull_request.head.ref");
            string? target = Text(payload, "pull_request.base.ref");
            string? number = Text(payload, "number");
            string? commit = Text(payload, "pull_request.head.sha") ?? Text(payload, "after") ?? "HEAD";
            return repo is null || source is null || target is null || !int.TryParse(number, out int pr)
                ? null
                : ParsedWebhook.PullRequest(provider, repo, action ?? "unknown", pr, source, target, commit, Text(payload, "pull_request.html_url"), Text(payload, "pull_request.title"));
        }

        return null;
    }

    private static ParsedWebhook? ParseGitLab(string eventType, JsonElement payload)
    {
        if (string.Equals(eventType, "push", StringComparison.OrdinalIgnoreCase))
        {
            string? branch = NormalizeRef(Text(payload, "ref"));
            string? repo = Text(payload, "project.path_with_namespace");
            string? commit = Text(payload, "after");
            return branch is null || repo is null
                ? null
                : ParsedWebhook.Push(WebhookProvider.GitLab, repo, branch, commit ?? "HEAD", CommitMessages(payload, "commits"), ChangedFiles(payload, "commits"));
        }

        if (string.Equals(eventType, "merge_request", StringComparison.OrdinalIgnoreCase))
        {
            string? repo = Text(payload, "project.path_with_namespace");
            string? source = Text(payload, "object_attributes.source_branch");
            string? target = Text(payload, "object_attributes.target_branch");
            string? number = Text(payload, "object_attributes.iid");
            string? commit = Text(payload, "object_attributes.last_commit.id") ?? "HEAD";
            return repo is null || source is null || target is null || !int.TryParse(number, out int pr)
                ? null
                : ParsedWebhook.PullRequest(WebhookProvider.GitLab, repo, Text(payload, "object_attributes.action") ?? "unknown", pr, source, target, commit, Text(payload, "object_attributes.url"), Text(payload, "object_attributes.title"));
        }

        return null;
    }

    private static ParsedWebhook? ParseBitbucket(string eventType, JsonElement payload)
    {
        if (string.Equals(eventType, "repo:push", StringComparison.OrdinalIgnoreCase))
        {
            string? branch = Text(payload, "push.changes.0.new.name");
            string? repo = Text(payload, "repository.full_name");
            string? commit = Text(payload, "push.changes.0.new.target.hash");
            return branch is null || repo is null
                ? null
                : ParsedWebhook.Push(WebhookProvider.Bitbucket, repo, branch, commit ?? "HEAD", CommitMessages(payload, "push.changes.0.commits"), []);
        }

        if (eventType.StartsWith("pullrequest:", StringComparison.OrdinalIgnoreCase))
        {
            string? repo = Text(payload, "repository.full_name");
            string? source = Text(payload, "pullrequest.source.branch.name");
            string? target = Text(payload, "pullrequest.destination.branch.name");
            string? number = Text(payload, "pullrequest.id");
            string? commit = Text(payload, "pullrequest.source.commit.hash") ?? "HEAD";
            return repo is null || source is null || target is null || !int.TryParse(number, out int pr)
                ? null
                : ParsedWebhook.PullRequest(WebhookProvider.Bitbucket, repo, eventType, pr, source, target, commit, Text(payload, "pullrequest.links.html.href"), Text(payload, "pullrequest.title"));
        }

        return null;
    }

    private static ParsedWebhook? ParseGeneric(JsonElement payload)
    {
        string? applicationId = Text(payload, "applicationId");
        string? commit = Text(payload, "commitSha") ?? Text(payload, "commit") ?? "HEAD";
        return Guid.TryParse(applicationId, out Guid parsed)
            ? ParsedWebhook.GenericDeploy(parsed, commit)
            : null;
    }

    private static string? NormalizeRef(string? value)
    {
        return value?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? value["refs/heads/".Length..] : value;
    }

    private static IReadOnlyList<string> CommitMessages(JsonElement payload, string path)
    {
        JsonElement? commits = ArrayElement(payload, path);
        return commits.HasValue
            ? commits.Value.EnumerateArray().Select(item => Text(item, "message")).Where(item => item is not null).Cast<string>().ToArray()
            : [];
    }

    private static IReadOnlyList<string> ChangedFiles(JsonElement payload, string path)
    {
        JsonElement? commits = ArrayElement(payload, path);
        if (!commits.HasValue) return [];
        return commits.Value.EnumerateArray()
            .SelectMany(commit => Files(commit, "added").Concat(Files(commit, "modified")).Concat(Files(commit, "removed")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> Files(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement files) && files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray().Select(file => file.GetString()).Where(file => file is not null).Cast<string>().ToArray()
            : [];
    }

    private static JsonElement? ArrayElement(JsonElement payload, string path)
    {
        JsonElement current = payload;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out int index))
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
    public static ParsedWebhook Push(WebhookProvider provider, string repository, string branch, string commitSha, IReadOnlyList<string> messages, IReadOnlyList<string> changedFiles) =>
        new(provider, "push", repository, branch, commitSha, null, null, null, null, null, null, null, messages, changedFiles);

    public static ParsedWebhook PullRequest(WebhookProvider provider, string repository, string action, int number, string sourceBranch, string targetBranch, string commitSha, string? url, string? title) =>
        new(provider, "pull_request", repository, targetBranch, commitSha, action, number, sourceBranch, targetBranch, url, title, null, title is null ? [] : [title], []);

    public static ParsedWebhook GenericDeploy(Guid applicationId, string commitSha) =>
        new(WebhookProvider.Generic, "generic", null, null, commitSha, null, null, null, null, null, null, applicationId, [], []);
}
