namespace Vessel.Domain.Proxy;

/// <summary>
///     Lifecycle of a proxy configuration version. <see cref="RolledBack" /> is a legacy value: a restore now creates a
///     new version (<see cref="ProxyConfigurationSource.Restore" />) instead of rewriting history.
/// </summary>
public enum ProxyConfigurationStatus
{
    Generated,
    Validated,
    Applied,
    Failed,
    RolledBack
}
