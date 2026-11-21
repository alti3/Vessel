namespace Vessel.Domain.Webhooks;

public enum ApplicationPreviewStatus
{
    /// <summary>The pull request is open and the preview is (or will be) deployed.</summary>
    Open = 0,

    /// <summary>The pull request closed or merged (or the preview was deleted); its runtime resources are removed.</summary>
    Archived = 1,

    /// <summary>The preview's containers were stopped through the preview API; a redeploy starts it again.</summary>
    Stopped = 2
}
