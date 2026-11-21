using Vessel.Application.Common;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;

namespace Vessel.Application.ManagedServices;

/// <summary>Redis-compatible flavor offered for the Redis engine. Valkey is the default (upstream v4.7.0).</summary>
public enum RedisFlavor
{
    Valkey = 0,
    Redis = 1
}

/// <summary>
///     Per-engine provisioning facts: the pinned default image (from <see cref="ComponentVersions" />), the legacy
///     repository used by databases created before images were pinned, the default data mount path, the variable
///     that carries the generated password and the container health probe.
/// </summary>
public sealed record DatabaseEngineProfile(
    DatabaseEngine Engine,
    string DefaultImage,
    string LegacyRepository,
    string DefaultMountPath,
    string PasswordVariable,
    IReadOnlyDictionary<string, string> FixedEnvironment,
    IReadOnlyList<string>? Command,
    string HealthProbe);

public static class DatabaseEngineProfiles
{
    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();

    // Probes run inside the container through a shell. Passwords are read from the container environment at probe
    // time ($$ escapes Compose interpolation), so they never appear in the health-check command line that
    // `docker inspect` shows. Redis-family probes require PONG because redis-cli exits 0 on error replies.
    private static readonly Dictionary<DatabaseEngine, DatabaseEngineProfile> Profiles = new()
    {
        [DatabaseEngine.PostgreSql] = new DatabaseEngineProfile(DatabaseEngine.PostgreSql,
            ComponentVersions.PostgresImage, "postgres", "/var/lib/postgresql", "POSTGRES_PASSWORD", NoEnvironment,
            null,
            "PGPASSWORD=\"$$POSTGRES_PASSWORD\" psql -U postgres -d postgres -tAc 'SELECT 1' >/dev/null || exit 1"),
        [DatabaseEngine.MySql] = new DatabaseEngineProfile(DatabaseEngine.MySql, ComponentVersions.MySqlImage,
            "mysql", "/var/lib/mysql", "MYSQL_ROOT_PASSWORD", NoEnvironment, null,
            "MYSQL_PWD=\"$$MYSQL_ROOT_PASSWORD\" mysqladmin ping -h 127.0.0.1 -u root --silent || exit 1"),
        [DatabaseEngine.MariaDb] = new DatabaseEngineProfile(DatabaseEngine.MariaDb, ComponentVersions.MariaDbImage,
            "mariadb", "/var/lib/mysql", "MYSQL_ROOT_PASSWORD", NoEnvironment, null,
            "healthcheck.sh --connect --innodb_initialized || exit 1"),
        [DatabaseEngine.Redis] = new DatabaseEngineProfile(DatabaseEngine.Redis, ComponentVersions.ValkeyImage,
            "redis", "/data", "REDIS_PASSWORD", NoEnvironment,
            ["redis-server", "--requirepass", "${REDIS_PASSWORD}"],
            "REDISCLI_AUTH=\"$$REDIS_PASSWORD\" redis-cli --no-auth-warning ping | grep -q PONG || exit 1"),
        [DatabaseEngine.KeyDb] = new DatabaseEngineProfile(DatabaseEngine.KeyDb, ComponentVersions.KeyDbImage,
            "eqalpha/keydb", "/data", "REDIS_PASSWORD", NoEnvironment,
            ["keydb-server", "--requirepass", "${REDIS_PASSWORD}"],
            "REDISCLI_AUTH=\"$$REDIS_PASSWORD\" keydb-cli --no-auth-warning ping | grep -q PONG || exit 1"),
        [DatabaseEngine.Dragonfly] = new DatabaseEngineProfile(DatabaseEngine.Dragonfly,
            ComponentVersions.DragonflyImage, "docker.dragonflydb.io/dragonflydb/dragonfly", "/data", "REDIS_PASSWORD",
            NoEnvironment, ["dragonfly", "--dir=/data", "--requirepass=${REDIS_PASSWORD}"],
            "REDISCLI_AUTH=\"$$REDIS_PASSWORD\" redis-cli --no-auth-warning ping | grep -q PONG || exit 1"),
        [DatabaseEngine.MongoDb] = new DatabaseEngineProfile(DatabaseEngine.MongoDb, ComponentVersions.MongoImage,
            "mongo", "/data/db", "MONGO_INITDB_ROOT_PASSWORD",
            new Dictionary<string, string> { ["MONGO_INITDB_ROOT_USERNAME"] = "root" }, null,
            "mongosh --quiet --norc --eval 'db.adminCommand({ ping: 1 }).ok' | grep -q 1 || exit 1"),
        [DatabaseEngine.ClickHouse] = new DatabaseEngineProfile(DatabaseEngine.ClickHouse,
            ComponentVersions.ClickHouseImage, "clickhouse/clickhouse-server", "/var/lib/clickhouse",
            "CLICKHOUSE_PASSWORD", new Dictionary<string, string> { ["CLICKHOUSE_USER"] = "default" }, null,
            "clickhouse-client --user \"$$CLICKHOUSE_USER\" --password \"$$CLICKHOUSE_PASSWORD\" --query 'SELECT 1' >/dev/null || exit 1")
    };

    public static IReadOnlyCollection<DatabaseEngineProfile> All => Profiles.Values;

    public static DatabaseEngineProfile For(DatabaseEngine engine)
    {
        return Profiles.TryGetValue(engine, out DatabaseEngineProfile? profile)
            ? profile
            : throw new DomainException($"Database engine {engine} is not supported.");
    }

    /// <summary>The pinned default image for new databases of an engine.</summary>
    public static string DefaultImage(DatabaseEngine engine, RedisFlavor redisFlavor = RedisFlavor.Valkey)
    {
        return engine == DatabaseEngine.Redis && redisFlavor == RedisFlavor.Redis
            ? ComponentVersions.RedisImage
            : For(engine).DefaultImage;
    }

    /// <summary>
    ///     The image a database runs: its pinned <see cref="DatabaseResource.Image" />, or for databases created before
    ///     images were pinned the legacy <c>{repository}:{Version}</c> reference, unchanged.
    /// </summary>
    public static string EffectiveImage(DatabaseResource database)
    {
        return database.Image?.Value ?? $"{For(database.Engine).LegacyRepository}:{database.Version.Value}";
    }

    /// <summary>
    ///     Resolves the image for a new database from an explicit image, a version/tag for the engine repository, or
    ///     the engine default. Floating references (no tag, <c>latest</c>) are rejected.
    /// </summary>
    public static DatabaseImage ResolveNewImage(DatabaseEngine engine, string? image, string? version,
        RedisFlavor redisFlavor = RedisFlavor.Valkey)
    {
        if (!string.IsNullOrWhiteSpace(image)) return DatabaseImage.Parse(image);
        if (!string.IsNullOrWhiteSpace(version))
        {
            var repository = engine == DatabaseEngine.Redis
                ? DatabaseImage.Parse(DefaultImage(engine, redisFlavor)).Repository
                : DatabaseImage.Parse(For(engine).DefaultImage).Repository;
            return DatabaseImage.Parse($"{repository}:{version.Trim()}");
        }

        return DatabaseImage.Parse(DefaultImage(engine, redisFlavor));
    }

    /// <summary>Default data directory for a database image (PostgreSQL 18+ moved its data under the parent path).</summary>
    public static string DefaultMountPath(DatabaseEngine engine, DatabaseImage image)
    {
        if (engine != DatabaseEngine.PostgreSql) return For(engine).DefaultMountPath;
        var major = image.Tag is { } tag ? new string(tag.TakeWhile(char.IsDigit).ToArray()) : string.Empty;
        return int.TryParse(major, out var version) && version < 18
            ? "/var/lib/postgresql/data"
            : "/var/lib/postgresql";
    }
}
