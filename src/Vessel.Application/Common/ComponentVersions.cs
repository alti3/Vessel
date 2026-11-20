namespace Vessel.Application.Common;

/// <summary>
///     Single manifest of third-party runtime component images that Vessel deploys onto managed servers.
///     Bump versions here only; every generator reads the pin from this class.
/// </summary>
public static class ComponentVersions
{
    /// <summary>
    ///     Traefik v3.7, latest patch. Verified against upstream Coolify <c>upstream.json</c>
    ///     (<c>"traefik": { "v3.7": "3.7.16" }</c>) on 2026-11-20.
    /// </summary>
    public const string Traefik = "3.7.16";

    public const string TraefikImage = "traefik:v" + Traefik;
}
