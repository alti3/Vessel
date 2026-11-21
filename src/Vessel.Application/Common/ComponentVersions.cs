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

    // Managed database defaults for new databases (existing databases keep their image). Verified against upstream
    // Coolify migrations 2026_11_06_083240_update_standalone_database_default_images and
    // 2026_11_06_113843_set_valkey_as_standalone_redis_default_image on 2026-11-20.

    public const string PostgresImage = "postgres:18-alpine";

    /// <summary>Default image for the Redis engine: Valkey, the Redis-compatible fork (upstream v4.7.0).</summary>
    public const string ValkeyImage = "valkey/valkey:9-alpine";

    /// <summary>Plain Redis stays selectable for the Redis engine.</summary>
    public const string RedisImage = "redis:8";

    public const string MongoImage = "mongo:9";

    public const string MySqlImage = "mysql:9.7";

    public const string MariaDbImage = "mariadb:12.3";

    public const string ClickHouseImage = "clickhouse/clickhouse-server:26.8";

    /// <summary>
    ///     KeyDB 6.3.4 (the final release). Upstream Coolify still defaults to <c>eqalpha/keydb:latest</c>; KeyDB only
    ///     publishes per-architecture version tags, so arm64 hosts should pick <c>eqalpha/keydb:arm64_v6.3.4</c> as a
    ///     custom image.
    /// </summary>
    public const string KeyDbImage = "eqalpha/keydb:x86_64_v6.3.4";

    /// <summary>Dragonfly; upstream Coolify leaves the tag floating, Vessel pins a release.</summary>
    public const string DragonflyImage = "docker.dragonflydb.io/dragonflydb/dragonfly:v1.34.1";

    /// <summary>Alternative PostgreSQL images offered by the upstream new-database page.</summary>
    public const string SupabasePostgresImage = "supabase/postgres:17.11.0.018";

    public const string PostgisImage = "postgis/postgis:18-3.7-alpine";

    // Service-template images that were floating (`latest`). Pins follow the upstream templates where they exist.

    /// <summary>cloudflared 2026.11.1 (upstream v4.7.6: eMudhra certificate compromise fix).</summary>
    public const string CloudflaredImage = "cloudflare/cloudflared:2026.11.1";

    public const string TimescaleImage = "timescale/timescaledb:2.22.0-pg17";

    public const string QdrantImage = "qdrant/qdrant:v1.20.1";

    public const string ChromaImage = "chromadb/chroma:1.5.9";
}
