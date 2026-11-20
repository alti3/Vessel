namespace Vessel.Domain.Webhooks;

public enum WebhookApplicationOutcomeKind
{
    /// <summary>A deployment was queued for the application.</summary>
    Queued = 0,

    /// <summary>An identical deployment was already queued or running; it is reused.</summary>
    Deduplicated = 1,

    /// <summary>The application was evaluated and not deployed (auto deploy off, watch paths, skip marker, ...).</summary>
    Skipped = 2,

    /// <summary>The pull request closed and the application's preview was archived and cleaned up.</summary>
    PreviewClosed = 3,

    /// <summary>Queueing failed for the application (for example a full deployment queue).</summary>
    Failed = 4
}

/// <summary>
///     Result of one webhook delivery for one verified matching application (DEP-12). Every application is evaluated
///     independently, so one application's skip never hides another application's deployment.
/// </summary>
public sealed record WebhookApplicationOutcome(
    Guid ApplicationId,
    WebhookApplicationOutcomeKind Outcome,
    Guid? DeploymentId,
    Guid? PreviewId,
    string? Reason);
