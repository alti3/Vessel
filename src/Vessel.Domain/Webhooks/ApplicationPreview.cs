using Vessel.Domain.Common;

namespace Vessel.Domain.Webhooks;

public sealed class ApplicationPreview : Entity<ApplicationPreviewId>
{
    private ApplicationPreview()
    {
    }

    private ApplicationPreview(
        ApplicationPreviewId id,
        ApplicationId applicationId,
        WebhookProvider provider,
        int pullRequestNumber,
        string sourceBranch,
        string targetBranch,
        string commitSha,
        string? pullRequestUrl,
        string? title,
        string? sourceRepository,
        bool isSameRepository,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        SourceRepository = sourceRepository;
        IsSameRepository = isSameRepository;
        ApplicationId = applicationId;
        Provider = provider;
        PullRequestNumber = pullRequestNumber;
        SourceBranch = sourceBranch;
        TargetBranch = targetBranch;
        CommitSha = commitSha;
        PullRequestUrl = pullRequestUrl;
        Title = title;
        Status = ApplicationPreviewStatus.Open;
    }

    public ApplicationId ApplicationId { get; private set; }

    public WebhookProvider Provider { get; private set; }

    public int PullRequestNumber { get; private set; }

    public string SourceBranch { get; private set; } = string.Empty;

    public string TargetBranch { get; private set; } = string.Empty;

    public string CommitSha { get; private set; } = string.Empty;

    public string? PullRequestUrl { get; private set; }

    public string? Title { get; private set; }

    public string? PreviewUrl { get; private set; }

    /// <summary>Provider full name of the pull request head repository (the fork for fork pull requests).</summary>
    public string? SourceRepository { get; private set; }

    /// <summary>
    ///     True when the pull request branch lives in the application repository itself. Only same-repository previews
    ///     may use repository-provided Docker Compose files.
    /// </summary>
    public bool IsSameRepository { get; private set; }

    public ApplicationPreviewStatus Status { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public static ApplicationPreview Open(
        ApplicationId applicationId,
        WebhookProvider provider,
        int pullRequestNumber,
        string sourceBranch,
        string targetBranch,
        string commitSha,
        string? pullRequestUrl,
        string? title,
        DateTimeOffset now,
        string? sourceRepository = null,
        bool isSameRepository = false)
    {
        return new ApplicationPreview(
            ApplicationPreviewId.New(),
            applicationId,
            provider,
            pullRequestNumber,
            DomainValidation.Required(sourceBranch, nameof(sourceBranch), 255),
            DomainValidation.Required(targetBranch, nameof(targetBranch), 255),
            DomainValidation.Required(commitSha, nameof(commitSha), 80),
            DomainValidation.Optional(pullRequestUrl, nameof(pullRequestUrl), 2048),
            DomainValidation.Optional(title, nameof(title), 255),
            DomainValidation.Optional(sourceRepository, nameof(sourceRepository), 512),
            isSameRepository,
            now);
    }

    public void Refresh(string sourceBranch, string targetBranch, string commitSha, string? pullRequestUrl,
        string? title, DateTimeOffset now, string? sourceRepository = null, bool isSameRepository = false)
    {
        SourceRepository = DomainValidation.Optional(sourceRepository, nameof(sourceRepository), 512);
        IsSameRepository = isSameRepository;
        SourceBranch = DomainValidation.Required(sourceBranch, nameof(sourceBranch), 255);
        TargetBranch = DomainValidation.Required(targetBranch, nameof(targetBranch), 255);
        CommitSha = DomainValidation.Required(commitSha, nameof(commitSha), 80);
        PullRequestUrl = DomainValidation.Optional(pullRequestUrl, nameof(pullRequestUrl), 2048);
        Title = DomainValidation.Optional(title, nameof(title), 255);
        Status = ApplicationPreviewStatus.Open;
        ClosedAt = null;
        Touch(now);
    }

    public void SetPreviewUrl(string? previewUrl, DateTimeOffset now)
    {
        PreviewUrl = DomainValidation.Optional(previewUrl, nameof(previewUrl), 2048);
        Touch(now);
    }

    /// <summary>Stops an open preview (its containers are removed, volumes kept). A redeploy reopens it.</summary>
    public void Stop(DateTimeOffset now)
    {
        if (Status == ApplicationPreviewStatus.Archived)
            throw new DomainException("Preview is closed.");
        Status = ApplicationPreviewStatus.Stopped;
        Touch(now);
    }

    /// <summary>Reopens a stopped preview for a redeploy. A closed (archived) preview reopens only with its pull request.</summary>
    public void Reopen(DateTimeOffset now)
    {
        if (Status == ApplicationPreviewStatus.Archived)
            throw new DomainException("Preview is closed; it reopens when its pull request is reopened.");
        Status = ApplicationPreviewStatus.Open;
        Touch(now);
    }

    public void Archive(DateTimeOffset now)
    {
        Status = ApplicationPreviewStatus.Archived;
        ClosedAt = now;
        Touch(now);
    }
}
