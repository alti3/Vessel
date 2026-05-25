using Vessel.Domain;

namespace Vessel.Application.Proxy;

public interface IProxyProvider
{
    ProxyConfigurationDocument Generate(ServerId serverId, IReadOnlyList<ProxyRoute> routes);

    ProxyValidationResult Validate(ProxyConfigurationDocument document);

    Task<ProxyApplyResult> ApplyAsync(
        ProxyConfigurationDocument document,
        ProxyConfigurationDocument? previous,
        CancellationToken cancellationToken = default);

    Task<ProxyApplyResult> ReloadAsync(ServerId serverId, CancellationToken cancellationToken = default);

    Task<ProxyApplyResult> RollbackAsync(
        ProxyConfigurationDocument previous,
        CancellationToken cancellationToken = default);
}
