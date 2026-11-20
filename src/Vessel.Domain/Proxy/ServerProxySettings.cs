using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Proxy;

/// <summary>
///     Per-server proxy lifecycle settings, keyed by the server id (one row per server). A server without a row
///     behaves as <see cref="CreateDefault" />: Traefik, ports 80/443, desired state running.
/// </summary>
public sealed partial class ServerProxySettings : Entity<ServerId>
{
    public const int MaxImageLength = 255;
    public const int MaxHashLength = 128;
    public const int MaxErrorLength = 2000;
    public const int DefaultHttpPort = 80;
    public const int DefaultHttpsPort = 443;

    private ServerProxySettings()
    {
    }

    private ServerProxySettings(ServerId serverId, string image, DateTimeOffset now)
        : base(serverId, now)
    {
        ProxyType = ServerProxyType.Traefik;
        Image = ValidateImage(image);
        HttpPort = DefaultHttpPort;
        HttpsPort = DefaultHttpsPort;
        DesiredState = ProxyDesiredState.Running;
    }

    public ServerId ServerId => Id;

    public ServerProxyType ProxyType { get; private set; }

    /// <summary>Pinned container image, re-pinned from the component versions manifest when the proxy (re)starts.</summary>
    public string Image { get; private set; } = string.Empty;

    public int HttpPort { get; private set; }

    public int HttpsPort { get; private set; }

    public ProxyDesiredState DesiredState { get; private set; }

    /// <summary>Hash of the static configuration and compose file the container was last started with.</summary>
    public string? LastAppliedStaticHash { get; private set; }

    /// <summary>Hash of the last dynamic (routes) configuration that was delivered and verified on the server.</summary>
    public string? LastAppliedDynamicHash { get; private set; }

    public DateTimeOffset? LastStartedAt { get; private set; }

    public DateTimeOffset? LastStoppedAt { get; private set; }

    public DateTimeOffset? LastDynamicAppliedAt { get; private set; }

    public string? LastError { get; private set; }

    public bool RoutingEnabled => ProxyType != ServerProxyType.None;

    public static ServerProxySettings CreateDefault(ServerId serverId, string image, DateTimeOffset now)
    {
        return new ServerProxySettings(serverId, image, now);
    }

    public void Configure(ServerProxyType proxyType, int httpPort, int httpsPort, DateTimeOffset now)
    {
        if (proxyType == ServerProxyType.Caddy)
            throw new DomainException("The Caddy proxy type is reserved and not supported yet.");
        if (!Enum.IsDefined(proxyType))
            throw new DomainException("Proxy type is invalid.");
        ValidatePort(httpPort, nameof(HttpPort));
        ValidatePort(httpsPort, nameof(HttpsPort));
        if (httpPort == httpsPort)
            throw new DomainException("Proxy HTTP and HTTPS ports must differ.");

        if (proxyType == ServerProxyType.None)
            DesiredState = ProxyDesiredState.Stopped;
        else if (ProxyType == ServerProxyType.None)
            DesiredState = ProxyDesiredState.Running;

        ProxyType = proxyType;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        Touch(now);
    }

    public void PinImage(string image, DateTimeOffset now)
    {
        var validated = ValidateImage(image);
        if (string.Equals(Image, validated, StringComparison.Ordinal))
            return;
        Image = validated;
        Touch(now);
    }

    public void MarkStarted(string staticHash, DateTimeOffset now)
    {
        if (!RoutingEnabled)
            throw new DomainException("A server with proxy type None has no proxy to start.");
        LastAppliedStaticHash = DomainValidation.Required(staticHash, nameof(staticHash), MaxHashLength);
        DesiredState = ProxyDesiredState.Running;
        LastStartedAt = now;
        LastError = null;
        Touch(now);
    }

    public void MarkStopped(DateTimeOffset now)
    {
        DesiredState = ProxyDesiredState.Stopped;
        LastStoppedAt = now;
        LastError = null;
        Touch(now);
    }

    public void RecordDynamicApplied(string dynamicHash, DateTimeOffset now)
    {
        LastAppliedDynamicHash = DomainValidation.Required(dynamicHash, nameof(dynamicHash), MaxHashLength);
        LastDynamicAppliedAt = now;
        Touch(now);
    }

    public void RecordFailure(string error, DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(error) ? "Proxy operation failed." : error.Trim();
        LastError = trimmed.Length > MaxErrorLength ? trimmed[..MaxErrorLength] : trimmed;
        Touch(now);
    }

    /// <summary>True when the container was started from a different static configuration than the current one.</summary>
    public bool RequiresRestart(string currentStaticHash)
    {
        return LastAppliedStaticHash is not null &&
               !string.Equals(LastAppliedStaticHash, currentStaticHash, StringComparison.Ordinal);
    }

    private static void ValidatePort(int port, string name)
    {
        if (port is < 1 or > 65535)
            throw new DomainException($"{name} must be between 1 and 65535.");
    }

    private static string ValidateImage(string image)
    {
        var value = DomainValidation.Required(image, nameof(Image), MaxImageLength);
        if (!ImagePattern().IsMatch(value))
            throw new DomainException("Proxy image reference is invalid.");
        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/:@-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();
}
