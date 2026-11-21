using Vessel.Domain.Common;

namespace Vessel.Domain.Operations;

public readonly record struct ControlPlaneBackupId(Guid Value) : IStronglyTypedId
{
    public static ControlPlaneBackupId New()
    {
        return new ControlPlaneBackupId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct UpgradeRunId(Guid Value) : IStronglyTypedId
{
    public static UpgradeRunId New()
    {
        return new UpgradeRunId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
///     Release channel the instance follows (upstream Coolify v4.6 <c>update_channel</c>). Lower values are more
///     stable; <see cref="Custom" /> pins one explicit version or image tag.
/// </summary>
public enum UpdateChannel
{
    Stable = 0,
    Preview = 1,
    Nightly = 2,
    Custom = 3
}

public static class UpdateChannels
{
    public static string ToManifestKey(UpdateChannel channel)
    {
        return channel switch
        {
            UpdateChannel.Stable => "stable",
            UpdateChannel.Preview => "preview",
            UpdateChannel.Nightly => "nightly",
            _ => throw new DomainException("A custom channel has no manifest entry; it pins one version.")
        };
    }

    /// <summary>True when moving from <paramref name="from" /> to <paramref name="to" /> makes the instance more stable.</summary>
    public static bool IsMoreStable(UpdateChannel from, UpdateChannel to)
    {
        return from != UpdateChannel.Custom && to != UpdateChannel.Custom && to < from;
    }
}
