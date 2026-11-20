namespace Vessel.Domain.Proxy;

/// <summary>Reverse proxy that Vessel manages on a server (upstream <c>ProxyTypes</c>: NONE, TRAEFIK, CADDY).</summary>
public enum ServerProxyType
{
    /// <summary>No managed proxy; Vessel skips routing for the server entirely.</summary>
    None = 0,

    Traefik = 1,

    /// <summary>Reserved for a future provider (gap NET-17); rejected by <see cref="ServerProxySettings" />.</summary>
    Caddy = 2
}
