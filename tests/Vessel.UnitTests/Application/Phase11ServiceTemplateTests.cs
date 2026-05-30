using Vessel.Application.ManagedServices;
using Vessel.Domain.Common;

namespace Vessel.UnitTests.Application;

public sealed class Phase11ServiceTemplateTests
{
    [Fact]
    public void Catalog_ListsInitialValidatedTemplates()
    {
        var catalog = new ServiceTemplateCatalog();

        IReadOnlyList<ServiceTemplateSummary> templates = catalog.List();

        Assert.Contains(templates, template => template.Key == "pgadmin");
        Assert.Contains(templates, template => template.Key == "redis-insight");
        Assert.Contains(templates, template => template.Key == "minio");
        Assert.Contains(templates, template => template.Key == "clickhouse");
        Assert.Contains(templates, template => template.Key == "tigerbeetle");
        Assert.Contains(templates, template => template.Key == "rustfs");
        Assert.Contains(templates, template => template.Key == "postgres-postgis");
        Assert.Contains(templates, template => template.Key == "postgres-timescale");
        Assert.Contains(templates, template => template.Key == "databasus");
        Assert.Contains(templates, template => template.Key == "valkey");
        Assert.Contains(templates, template => template.Key == "keydb");
        Assert.Contains(templates, template => template.Key == "dragonfly");
        Assert.Contains(templates, template => template.Key == "elasticsearch");
        Assert.Contains(templates, template => template.Key == "elasticsearch-kibana");
        Assert.Contains(templates, template => template.Key == "chromadb");
        Assert.Contains(templates, template => template.Key == "mediawiki");
        Assert.Contains(templates, template => template.Key == "pihole");
        Assert.Contains(templates, template => template.Key == "hoppscotch");
        Assert.Contains(templates, template => template.Key == "proxyscotch");
        Assert.Contains(templates, template => template.Key == "strapi");
        Assert.Contains(templates, template => template.Key == "supabase");
        Assert.Contains(templates, template => template.Key == "wireguard-easy");
        Assert.Contains(templates, template => template.Key == "wordpress-without-database");
        Assert.Contains(templates, template => template.Key == "wordpress-mariadb");
        Assert.Contains(templates, template => template.Key == "wordpress-mysql");
        Assert.Contains(templates, template => template.Key == "tailscale-client");
        Assert.Contains(templates, template => template.Key == "qdrant");
        Assert.Contains(templates, template => template.Key == "qbittorrent");
        Assert.Contains(templates, template => template.Key == "rabbitmq");
        Assert.Contains(templates, template => template.Key == "portainer");
        Assert.Contains(templates, template => template.Key == "open-webui");
        Assert.Contains(templates, template => template.Key == "openclaw");
        Assert.Contains(templates, template => template.Key == "n8n");
        Assert.Contains(templates, template => template.Key == "excalidraw");
        Assert.Contains(templates, template => template.Key == "gitlab");
        Assert.Contains(templates, template => template.Key == "gitea");
        Assert.Contains(templates, template => template.Key == "gitea-mariadb");
        Assert.Contains(templates, template => template.Key == "gitea-postgres");
        Assert.Contains(templates, template => template.Key == "gitea-mysql");
        Assert.Contains(templates, template => template.Key == "grafana");
        Assert.Contains(templates, template => template.Key == "grafana-postgres");
        Assert.Contains(templates, template => template.Key == "home-assistant");
        Assert.Contains(templates, template => template.Key == "minecraft-server");
        Assert.Contains(templates, template => template.Key == "moodle");
        Assert.Contains(templates, template => template.Key == "matrix-synapse-postgres");
        Assert.Contains(templates, template => template.Key == "matrix-synapse-sqlite");
        Assert.Contains(templates, template => template.Key == "marimo");
        Assert.Contains(templates, template => template.Key == "jupyter-notebook-python");
        Assert.Contains(templates, template => template.Key == "drizzle-gateway");
        Assert.Contains(templates, template => template.Key == "drupal-postgres");
        Assert.Contains(templates, template => template.Key == "electricsql");
        Assert.Contains(templates, template => template.Key == "codimd");
        Assert.Contains(templates, template => template.Key == "librechat");
        Assert.Contains(templates, template => template.Key == "mailpit");
        Assert.Contains(templates, template => template.Key == "nextcloud");
        Assert.Contains(templates, template => template.Key == "nextcloud-postgres");
        Assert.Contains(templates, template => template.Key == "nextcloud-mysql");
        Assert.Contains(templates, template => template.Key == "nextcloud-mariadb");
        Assert.Contains(templates, template => template.Key == "odoo");
        Assert.Contains(templates, template => template.Key == "pocketbase");
        Assert.Contains(templates, template => template.Key == "wikijs");
        Assert.Contains(templates, template => template.Key == "keycloak");
        Assert.Contains(templates, template => template.Key == "keycloak-postgres");
        Assert.Contains(templates, template => template.Key == "cloudflared");
        Assert.Contains(templates, template => template.Key == "metabase");
    }

    [Fact]
    public void CreatePlan_RejectsMissingRequiredSecretInput()
    {
        var catalog = new ServiceTemplateCatalog();

        Assert.Throws<DomainException>(() => catalog.CreatePlan(
            "pgadmin",
            Guid.NewGuid(),
            "pgAdmin",
            new Dictionary<string, string> { ["email"] = "admin@example.com" }));
    }

    [Fact]
    public void CreatePlan_RedactsSecretInputsFromPlanMetadata()
    {
        var catalog = new ServiceTemplateCatalog();

        ServiceProvisioningPlan plan = catalog.CreatePlan(
            "minio",
            Guid.NewGuid(),
            "MinIO Storage",
            new Dictionary<string, string>
            {
                ["rootUser"] = "admin",
                ["rootPassword"] = "secret-value"
            });

        Assert.Contains("quay.io/minio/minio", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("${ROOTPASSWORD}", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Equal("secret-value", plan.SecretValues["rootPassword"]);
        Assert.DoesNotContain("MinIO Storage", plan.ProjectName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("clickhouse", "clickhouse/clickhouse-server:25.11", "8123:8123", "9000:9000", "CLICKHOUSE_PASSWORD")]
    [InlineData("rustfs", "rustfs/rustfs:latest", "9000:9000", "9001:9001", "RUSTFS_SECRET_KEY")]
    [InlineData("postgres-postgis", "postgis/postgis:17-3.5", "5432:5432", "postgis-data", "POSTGRES_PASSWORD")]
    [InlineData("postgres-timescale", "timescale/timescaledb:latest-pg17", "5432:5432", "timescaledb-data",
        "POSTGRES_PASSWORD")]
    public void CreatePlan_EmitsDatabaseAndStorageTemplates(
        string templateKey,
        string image,
        string expectedFirst,
        string expectedSecond,
        string secretEnvironmentKey)
    {
        var catalog = new ServiceTemplateCatalog();

        ServiceProvisioningPlan plan = catalog.CreatePlan(
            templateKey,
            Guid.NewGuid(),
            templateKey,
            new Dictionary<string, string>
            {
                ["password"] = "database-secret",
                ["secretKey"] = "database-secret"
            });

        Assert.Contains(image, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(expectedFirst, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(expectedSecond, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(secretEnvironmentKey, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("database-secret", plan.ComposeYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_EmitsTigerBeetleFormatAndStartServices()
    {
        var catalog = new ServiceTemplateCatalog();

        ServiceProvisioningPlan plan = catalog.CreatePlan(
            "tigerbeetle",
            Guid.NewGuid(),
            "TigerBeetle Ledger",
            new Dictionary<string, string>());

        Assert.Contains("ghcr.io/tigerbeetle/tigerbeetle:latest", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("format --cluster=0 --replica=0 --replica-count=1", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("start --addresses=0.0.0.0:3000", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("seccomp=unconfined", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("3000:3000", plan.ComposeYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlan_EmitsDatabasusTemplate()
    {
        var catalog = new ServiceTemplateCatalog();

        ServiceProvisioningPlan plan = catalog.CreatePlan(
            "databasus",
            Guid.NewGuid(),
            "Databasus",
            new Dictionary<string, string>());

        Assert.Contains("databasus/databasus:latest", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("4005:4005", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("databasus-data:/databasus-data", plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains("http://localhost:4005/api/v1/system/health", plan.ComposeYaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("redis-insight", "redis/redisinsight:latest", "5540:5540", "redis-insight-data:/data",
        "RI_ENCRYPTION_KEY")]
    [InlineData("qdrant", "qdrant/qdrant:latest", "6333:6333", "qdrant-storage:/qdrant/storage",
        "QDRANT__SERVICE__API_KEY")]
    [InlineData("valkey", "valkey/valkey:latest", "6379:6379", "valkey-data:/data", "VALKEY_PASSWORD")]
    [InlineData("keydb", "eqalpha/keydb:latest", "6379:6379", "keydb-data:/data", "KEYDB_PASSWORD")]
    [InlineData("dragonfly", "docker.dragonflydb.io/dragonflydb/dragonfly:latest", "6379:6379", "dragonfly-data:/data",
        "DRAGONFLY_PASSWORD")]
    [InlineData("elasticsearch", "docker.elastic.co/elasticsearch/elasticsearch:8.19.0", "9200:9200",
        "elasticsearch-data:/usr/share/elasticsearch/data", "ELASTIC_PASSWORD")]
    [InlineData("rabbitmq", "rabbitmq:4-management", "15672:15672", "rabbitmq-data:/var/lib/rabbitmq",
        "RABBITMQ_DEFAULT_PASS")]
    [InlineData("pihole", "pihole/pihole:latest", "53:53/udp", "pihole-etc:/etc/pihole",
        "FTLCONF_webserver_api_password")]
    [InlineData("proxyscotch", "hoppscotch/proxyscotch:v0.1.4", "9159:9159", "PROXYSCOTCH_ALLOWED_ORIGINS",
        "PROXYSCOTCH_TOKEN")]
    [InlineData("wireguard-easy", "ghcr.io/wg-easy/wg-easy:latest", "51820:51820/udp", "wg-easy-data:/etc/wireguard",
        "PASSWORD")]
    [InlineData("tailscale-client", "tailscale/tailscale:latest", "/dev/net/tun:/dev/net/tun",
        "tailscale-client-data:/var/lib/tailscale", "TS_AUTHKEY")]
    [InlineData("open-webui", "ghcr.io/open-webui/open-webui:main", "3000:8080", "open-webui-data:/app/backend/data",
        "OPENAI_API_KEY")]
    [InlineData("openclaw", "coollabsio/openclaw:2026.2.6", "8083:8080", "openclaw-data:/data",
        "OPENCLAW_GATEWAY_TOKEN")]
    [InlineData("n8n", "n8nio/n8n:latest", "5678:5678", "n8n-data:/home/node/.n8n", "N8N_ENCRYPTION_KEY")]
    [InlineData("gitlab", "gitlab/gitlab-ce:latest", "2222:22", "gitlab-data:/var/opt/gitlab", "GITLAB_ROOT_PASSWORD")]
    [InlineData("grafana", "grafana/grafana-oss:latest", "3004:3000", "grafana-data:/var/lib/grafana",
        "GF_SECURITY_ADMIN_PASSWORD")]
    [InlineData("minecraft-server", "itzg/minecraft-server:latest", "25565:25565", "minecraft-data:/data",
        "RCON_PASSWORD")]
    [InlineData("marimo", "ghcr.io/marimo-team/marimo:latest", "2718:2718", "marimo-workspace:/workspace",
        "MARIMO_PASSWORD")]
    [InlineData("jupyter-notebook-python", "quay.io/jupyter/base-notebook:latest", "8888:8888",
        "jupyter-notebook-python-work:/home/jovyan/work", "JUPYTER_TOKEN")]
    [InlineData("drizzle-gateway", "ghcr.io/drizzle-team/gateway:latest", "4983:4983",
        "drizzle-gateway-data:/app", "MASTER_PASSWORD")]
    [InlineData("electricsql", "electricsql/electric:latest", "5133:3000", "ELECTRIC_SECRET",
        "DATABASE_URL")]
    [InlineData("nextcloud", "nextcloud:latest", "8088:80", "nextcloud-data:/var/www/html",
        "NEXTCLOUD_ADMIN_PASSWORD")]
    [InlineData("keycloak", "quay.io/keycloak/keycloak:latest", "8087:8080", "KC_BOOTSTRAP_ADMIN_USERNAME",
        "KC_BOOTSTRAP_ADMIN_PASSWORD")]
    [InlineData("cloudflared", "cloudflare/cloudflared:latest", "tunnel --no-autoupdate run", "restart: unless-stopped",
        "TOKEN")]
    [InlineData("metabase", "metabase/metabase:latest", "3005:3000", "metabase-data:/metabase-data",
        "MB_ENCRYPTION_SECRET_KEY")]
    public void CreatePlan_EmitsInfrastructureTemplates(
        string templateKey,
        string image,
        string expectedFirst,
        string expectedSecond,
        string secretEnvironmentKey)
    {
        var catalog = new ServiceTemplateCatalog();

        ServiceProvisioningPlan plan = catalog.CreatePlan(
            templateKey,
            Guid.NewGuid(),
            templateKey,
            new Dictionary<string, string>
            {
                ["password"] = "infrastructure-secret",
                ["token"] = "infrastructure-secret",
                ["authKey"] = "infrastructure-secret",
                ["apiKey"] = "infrastructure-secret",
                ["encryptionKey"] = "infrastructure-secret",
                ["openAiApiKey"] = "infrastructure-secret",
                ["gatewayToken"] = "infrastructure-secret",
                ["rootPassword"] = "infrastructure-secret",
                ["adminPassword"] = "infrastructure-secret",
                ["rconPassword"] = "infrastructure-secret",
                ["masterPassword"] = "infrastructure-secret",
                ["databaseUrl"] = "infrastructure-secret",
                ["electricSecret"] = "infrastructure-secret"
            });

        Assert.Contains(image, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(expectedFirst, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(expectedSecond, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(secretEnvironmentKey, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("infrastructure-secret", plan.ComposeYaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("elasticsearch-kibana", "docker.elastic.co/kibana/kibana:8.19.0", "5601:5601",
        "kibana-data:/usr/share/kibana/data")]
    [InlineData("chromadb", "chromadb/chroma:latest", "8000:8000", "chromadb-data:/data")]
    [InlineData("mediawiki", "mediawiki:latest", "8080:80", "mediawiki-images:/var/www/html/images")]
    [InlineData("qbittorrent", "lscr.io/linuxserver/qbittorrent:latest", "8080:8080", "qbittorrent-config:/config")]
    [InlineData("portainer", "portainer/portainer-ce:alpine", "9443:9443", "portainer-data:/data")]
    [InlineData("wordpress-without-database", "wordpress:latest", "8082:80", "wordpress-files:/var/www/html")]
    [InlineData("excalidraw", "excalidraw/excalidraw:latest", "8084:80", "http://localhost")]
    [InlineData("gitea", "gitea/gitea:latest", "3003:3000", "gitea-data:/data")]
    [InlineData("home-assistant", "ghcr.io/home-assistant/home-assistant:stable", "8123:8123",
        "homeassistant-config:/config")]
    [InlineData("matrix-synapse-sqlite", "matrixdotorg/synapse:latest", "8008:8008", "synapse-data:/data")]
    [InlineData("mailpit", "axllent/mailpit:latest", "8025:8025", "1025:1025")]
    [InlineData("pocketbase", "ghcr.io/muchobien/pocketbase:latest", "8090:8090", "pocketbase-data:/pb_data")]
    public void CreatePlan_EmitsNoSecretApplicationTemplates(
        string templateKey,
        string image,
        string expectedFirst,
        string expectedSecond)
    {
        var catalog = new ServiceTemplateCatalog();

        ServiceProvisioningPlan plan = catalog.CreatePlan(
            templateKey,
            Guid.NewGuid(),
            templateKey,
            templateKey == "elasticsearch-kibana"
                ? new Dictionary<string, string> { ["password"] = "elastic-secret" }
                : new Dictionary<string, string>());

        Assert.Contains(image, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(expectedFirst, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(expectedSecond, plan.ComposeYaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hoppscotch", "hoppscotch/hoppscotch:2026.2.1", "3000:80",
        "hoppscotch-postgres-data:/var/lib/postgresql/data")]
    [InlineData("strapi", "elestio/strapi-production:v5.33.4", "1337:1337",
        "strapi-postgres-data:/var/lib/postgresql/data")]
    [InlineData("supabase", "supabase/studio:latest", "3001:3000", "supabase-db-data:/var/lib/postgresql/data")]
    [InlineData("wordpress-mariadb", "mariadb:11", "8082:80", "mariadb-data:/var/lib/mysql")]
    [InlineData("wordpress-mysql", "mysql:8", "8082:80", "mysql-data:/var/lib/mysql")]
    [InlineData("gitea-mariadb", "mariadb:11", "3003:3000", "gitea-mariadb-data:/var/lib/mysql")]
    [InlineData("gitea-postgres", "postgres:16-alpine", "3003:3000", "gitea-postgresql-data:/var/lib/postgresql/data")]
    [InlineData("gitea-mysql", "mysql:8.0", "3003:3000", "gitea-mysql-data:/var/lib/mysql")]
    [InlineData("grafana-postgres", "postgres:16-alpine", "3004:3000",
        "grafana-postgresql-data:/var/lib/postgresql/data")]
    [InlineData("moodle", "bitnami/moodle:latest", "8086:8080", "moodle-mariadb-data:/bitnami/mariadb")]
    [InlineData("matrix-synapse-postgres", "postgres:16-alpine", "8008:8008",
        "synapse-postgresql-data:/var/lib/postgresql/data")]
    [InlineData("keycloak-postgres", "quay.io/keycloak/keycloak:latest", "8087:8080",
        "keycloak-postgresql-data:/var/lib/postgresql/data")]
    [InlineData("drupal-postgres", "drupal:latest", "8089:80", "drupal-postgresql-data:/var/lib/postgresql/data")]
    [InlineData("codimd", "quay.io/hedgedoc/hedgedoc:latest", "3006:3000",
        "codimd-postgresql-data:/var/lib/postgresql/data")]
    [InlineData("librechat", "ghcr.io/danny-avila/librechat:latest", "3080:3080",
        "librechat-mongodb-data:/data/db")]
    [InlineData("nextcloud-postgres", "postgres:16-alpine", "8088:80",
        "nextcloud-postgresql-data:/var/lib/postgresql/data")]
    [InlineData("nextcloud-mysql", "mysql:8", "8088:80", "nextcloud-mysql-data:/var/lib/mysql")]
    [InlineData("nextcloud-mariadb", "mariadb:11", "8088:80", "nextcloud-mariadb-data:/var/lib/mysql")]
    [InlineData("odoo", "odoo:latest", "8069:8069", "odoo-postgresql-data:/var/lib/postgresql/data")]
    [InlineData("wikijs", "requarks/wiki:2", "3007:3000", "wikijs-postgresql-data:/var/lib/postgresql/data")]
    public void CreatePlan_EmitsDatabaseBackedApplicationTemplates(
        string templateKey,
        string image,
        string expectedFirst,
        string expectedSecond)
    {
        var catalog = new ServiceTemplateCatalog();

        ServiceProvisioningPlan plan = catalog.CreatePlan(
            templateKey,
            Guid.NewGuid(),
            templateKey,
            new Dictionary<string, string>
            {
                ["postgresPassword"] = "app-secret",
                ["dataEncryptionKey"] = "app-secret",
                ["databasePassword"] = "app-secret",
                ["rootPassword"] = "app-secret",
                ["appKeys"] = "app-secret",
                ["jwtSecret"] = "app-secret",
                ["adminJwtSecret"] = "app-secret",
                ["anonKey"] = "app-secret",
                ["serviceRoleKey"] = "app-secret",
                ["dashboardPassword"] = "app-secret",
                ["adminPassword"] = "app-secret",
                ["sessionSecret"] = "app-secret",
                ["jwtRefreshSecret"] = "app-secret",
                ["credsKey"] = "app-secret",
                ["credsIv"] = "app-secret",
                ["meiliMasterKey"] = "app-secret"
            });

        Assert.Contains(image, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(expectedFirst, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.Contains(expectedSecond, plan.ComposeYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("app-secret", plan.ComposeYaml, StringComparison.Ordinal);
    }
}
