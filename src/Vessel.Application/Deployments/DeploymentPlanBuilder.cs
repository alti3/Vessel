using System.Globalization;
using Vessel.Application.Git;
using Vessel.Application.Proxy;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.ValueObjects;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Deployments;

/// <summary>
///     Runtime names of one deployment stack. Production and each preview get disjoint names: previews use their own
///     compose project (so they never replace production), their own service name and upstream alias (never the display
///     name) and a <c>pr-{n}</c> suffix that namespaces container and volume names. The suffix is null for production.
/// </summary>
public sealed record DeploymentNaming(
    string ProjectName,
    string ServiceName,
    string ImageName,
    string NetworkName,
    string UpstreamAlias,
    string? PreviewSuffix)
{
    public bool IsPreview => PreviewSuffix is not null;
}

/// <summary>
///     Variables passed to a deployment (production values first, then preview copies, later wins) and the production
///     keys a preview does not receive (logged by key only, so users know what to add).
/// </summary>
public sealed record DeploymentVariableSelection(
    IReadOnlyList<EnvironmentVariable> Variables,
    IReadOnlyList<string> WithheldKeys,
    bool UsesProductionFallback);

/// <summary>
///     Pure planning rules for a deployment: stack naming, git checkout, preview routing and preview variables.
///     Kept free of I/O so production/preview isolation is unit tested.
/// </summary>
public static class DeploymentPlanBuilder
{
    public const int DefaultPort = 8080;

    public static DeploymentNaming Naming(AppId applicationId, ServerId serverId, DeploymentId deploymentId,
        ApplicationPreview? preview)
    {
        var app = applicationId.Value.ToString("N");
        var network = $"vessel-{serverId.Value:N}";
        if (preview is null)
        {
            var alias = ProxyNaming.UpstreamAlias(applicationId);
            return new DeploymentNaming($"vessel-{app}", alias, $"vessel/{app}:{deploymentId.Value:N}", network,
                alias, null);
        }

        if (preview.ApplicationId != applicationId)
            throw new DomainException("Preview does not belong to the deployed application.");

        var suffix = PreviewSuffix(preview.PullRequestNumber);
        var previewAlias = ProxyNaming.PreviewUpstreamAlias(applicationId, preview.PullRequestNumber);
        return new DeploymentNaming($"vessel-{app}-{suffix}", previewAlias,
            $"vessel/{app}:{suffix}-{deploymentId.Value:N}", network, previewAlias, suffix);
    }

    public static string PreviewSuffix(int pullRequestNumber)
    {
        if (pullRequestNumber <= 0) throw new DomainException("Pull request number must be positive.");
        return "pr-" + pullRequestNumber.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Provider ref that holds a pull request head in the target repository, so fork commits are reachable:
    ///     GitHub and Gitea <c>refs/pull/{n}/head</c>, GitLab <c>refs/merge-requests/{n}/head</c>. Bitbucket has no PR refs,
    ///     so its source branch is fetched (same-repository pull requests only).
    /// </summary>
    public static string PullRequestFetchRef(WebhookProvider provider, int pullRequestNumber, string sourceBranch)
    {
        if (pullRequestNumber <= 0) throw new DomainException("Pull request number must be positive.");
        var number = pullRequestNumber.ToString(CultureInfo.InvariantCulture);
        return provider switch
        {
            WebhookProvider.GitHub or WebhookProvider.Gitea => $"refs/pull/{number}/head",
            WebhookProvider.GitLab => $"refs/merge-requests/{number}/head",
            WebhookProvider.Bitbucket => GitRef.Parse(sourceBranch, "pull request source branch").Value,
            _ => throw new DomainException($"Preview deployments are not supported for provider {provider}.")
        };
    }

    /// <summary>
    ///     Builds the checkout for a deployment. Refs are validated here (at start) in addition to queue time.
    ///     Previews fetch the provider PR ref and pin the PR head commit; production deploys the requested commit,
    ///     the application's pinned commit, or the branch head.
    /// </summary>
    public static GitCheckoutRequest Checkout(AppEntity application, Deployment deployment,
        ApplicationPreview? preview, string targetDirectory)
    {
        var repositoryUrl = new Uri(application.GitSource.RepositoryUrl.Value);
        GitRef branch = GitRef.Parse(application.GitSource.Branch, "application branch");

        if (preview is not null)
        {
            if (preview.ApplicationId != application.Id)
                throw new DomainException("Preview does not belong to the deployed application.");
            var expected = FirstCommitSha(deployment.CommitSha, preview.CommitSha);
            return new GitCheckoutRequest(repositoryUrl, targetDirectory, GitCheckoutKind.PullRequest,
                PullRequestFetchRef(preview.Provider, preview.PullRequestNumber, preview.SourceBranch),
                null, expected);
        }

        var requested = IsUnresolved(deployment.CommitSha) ? application.GitSource.CommitSha : deployment.CommitSha;
        if (IsUnresolved(requested))
            return new GitCheckoutRequest(repositoryUrl, targetDirectory, GitCheckoutKind.Named, branch.Value);

        GitRef reference = GitRef.Parse(requested, "deployment commit or ref");
        return reference.IsCommitSha
            ? new GitCheckoutRequest(repositoryUrl, targetDirectory, GitCheckoutKind.Commit, reference.Value,
                branch.Value)
            : new GitCheckoutRequest(repositoryUrl, targetDirectory, GitCheckoutKind.Named, reference.Value);
    }

    /// <summary>
    ///     Validates a commit or ref supplied at queue time (UI, API or webhook). Returns null for "use the branch head".
    /// </summary>
    public static string? NormalizeRequestedRef(string? value)
    {
        if (IsUnresolved(value)) return null;
        return GitRef.Parse(value, "commit or ref").Value;
    }

    /// <summary>
    ///     Preview host derived from the application's canonical (or first) domain using Coolify's default preview
    ///     template <c>{{pr_id}}.{{domain}}</c>. Returns null when the application has no domain to derive from.
    /// </summary>
    public static PreviewRoute? PreviewRoute(IEnumerable<ApplicationDomain> domains, int pullRequestNumber,
        int defaultPort)
    {
        ApplicationDomain? domain = domains
            .OrderByDescending(item => item.Canonical)
            .ThenBy(item => item.DomainName.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (domain is null) return null;

        DomainName host;
        try
        {
            host = new DomainName($"{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}.{domain.DomainName.Value}");
        }
        catch (DomainException)
        {
            return null;
        }

        var scheme = domain.TlsEnabled ? "https" : "http";
        return new PreviewRoute(host.Value, $"{scheme}://{host.Value}", domain.TargetPort ?? defaultPort,
            domain.TlsEnabled);
    }

    /// <summary>
    ///     Selects variables per Coolify's preview rules. Production deployments never receive preview copies. A preview
    ///     always receives its preview copies; it falls back to production runtime values (for keys without a preview
    ///     copy) only when public/fork previews are off and at least one preview copy exists. Public previews never
    ///     receive production values.
    /// </summary>
    public static DeploymentVariableSelection SelectVariables(IReadOnlyList<EnvironmentVariable> applicable,
        bool isPreview, bool publicPreviewsEnabled)
    {
        EnvironmentVariable[] production = applicable.Where(variable => !variable.IsPreview).ToArray();
        if (!isPreview) return new DeploymentVariableSelection(production, [], false);

        EnvironmentVariable[] previewCopies = applicable.Where(variable => variable.IsPreview).ToArray();
        var previewKeys = previewCopies.Select(variable => variable.Key.Value).ToHashSet(StringComparer.Ordinal);
        var fallback = !publicPreviewsEnabled && previewCopies.Length > 0;
        EnvironmentVariable[] inherited = fallback
            ? production.Where(variable => !previewKeys.Contains(variable.Key.Value)).ToArray()
            : [];
        var passedKeys = previewKeys.Concat(inherited.Select(variable => variable.Key.Value))
            .ToHashSet(StringComparer.Ordinal);
        var withheld = production.Select(variable => variable.Key.Value)
            .Where(key => !passedKeys.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        return new DeploymentVariableSelection([.. inherited, .. previewCopies], withheld, fallback);
    }

    private static string? FirstCommitSha(params string?[] candidates)
    {
        return candidates.FirstOrDefault(GitRef.IsCommitShaValue);
    }

    private static bool IsUnresolved(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               string.Equals(value.Trim(), "pending", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value.Trim(), "HEAD", StringComparison.Ordinal);
    }
}

/// <summary>Routing record of one preview: its own host and URL, never a production host.</summary>
public sealed record PreviewRoute(string Host, string Url, int TargetPort, bool TlsEnabled);
