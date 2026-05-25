using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Application.ManagedServices;

public sealed partial class ServiceTemplateCatalog
{
    private static readonly ServiceTemplateDefinition[] Templates =
    [
        new(
            "pgadmin",
            "pgAdmin",
            "Web administration for PostgreSQL servers.",
            "1",
            [
                new ServiceTemplateInput("email", "Admin email", true, false, "admin@example.com"),
                new ServiceTemplateInput("password", "Admin password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          pgadmin:
                            image: dpage/pgadmin4:latest
                            environment:
                              PGADMIN_DEFAULT_EMAIL: {{Escape(inputs["email"])}}
                              PGADMIN_DEFAULT_PASSWORD: {{Escape(inputs["password"])}}
                            ports:
                              - "5050:80"
                            restart: unless-stopped
                        """),
        new(
            "redis-insight",
            "Redis Insight",
            "Browser UI for Redis databases.",
            "1",
            [new ServiceTemplateInput("encryptionKey", "Encryption key", true, true, null)],
            inputs => $$"""
                        services:
                          redis-insight:
                            image: redis/redisinsight:latest
                            environment:
                              RI_APP_HOST: 0.0.0.0
                              RI_APP_PORT: "5540"
                              RI_ENCRYPTION_KEY: {{Escape(inputs["encryptionKey"])}}
                              RI_LOG_LEVEL: info
                            volumes:
                              - redis-insight-data:/data
                            ports:
                              - "5540:5540"
                            healthcheck:
                              test: ["CMD", "wget", "--spider", "http://0.0.0.0:5540/api/health"]
                              interval: 10s
                              timeout: 10s
                              retries: 3
                            restart: unless-stopped
                        volumes:
                          redis-insight-data:
                        """),
        new(
            "qdrant",
            "Qdrant",
            "Vector similarity search database.",
            "1",
            [new ServiceTemplateInput("apiKey", "API key", true, true, null)],
            inputs => $$"""
                        services:
                          qdrant:
                            image: qdrant/qdrant:latest
                            environment:
                              QDRANT__SERVICE__API_KEY: {{Escape(inputs["apiKey"])}}
                            volumes:
                              - qdrant-storage:/qdrant/storage
                            ports:
                              - "6333:6333"
                              - "6334:6334"
                            healthcheck:
                              test: ["CMD-SHELL", "bash -c ':> /dev/tcp/127.0.0.1/6333' || exit 1"]
                              interval: 5s
                              timeout: 5s
                              retries: 3
                            restart: unless-stopped
                        volumes:
                          qdrant-storage:
                        """),
        new(
            "qbittorrent",
            "qBittorrent",
            "BitTorrent client with a web UI.",
            "1",
            [
                new ServiceTemplateInput("puid", "User ID", true, false, "1000"),
                new ServiceTemplateInput("pgid", "Group ID", true, false, "1000"),
                new ServiceTemplateInput("timezone", "Timezone", true, false, "Etc/UTC"),
                new ServiceTemplateInput("webUiPort", "Web UI port", true, false, "8080"),
                new ServiceTemplateInput("torrentingPort", "Torrenting port", true, false, "6881")
            ],
            inputs => $$"""
                        services:
                          qbittorrent:
                            image: lscr.io/linuxserver/qbittorrent:latest
                            environment:
                              PUID: {{Escape(inputs["puid"])}}
                              PGID: {{Escape(inputs["pgid"])}}
                              TZ: {{Escape(inputs["timezone"])}}
                              WEBUI_PORT: {{Escape(inputs["webUiPort"])}}
                              TORRENTING_PORT: {{Escape(inputs["torrentingPort"])}}
                            volumes:
                              - qbittorrent-config:/config
                              - qbittorrent-downloads:/downloads
                            ports:
                              - "{{Escape(inputs["webUiPort"])}}:{{Escape(inputs["webUiPort"])}}"
                              - "{{Escape(inputs["torrentingPort"])}}:{{Escape(inputs["torrentingPort"])}}"
                              - "{{Escape(inputs["torrentingPort"])}}:{{Escape(inputs["torrentingPort"])}}/udp"
                            healthcheck:
                              test: ["CMD", "wget", "-q", "--spider", "http://127.0.0.1:{{Escape(inputs["webUiPort"])}}/"]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          qbittorrent-config:
                          qbittorrent-downloads:
                        """),
        new(
            "rabbitmq",
            "RabbitMQ",
            "Message broker with the RabbitMQ management UI.",
            "1",
            [
                new ServiceTemplateInput("username", "Username", true, false, "rabbitmq"),
                new ServiceTemplateInput("password", "Password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          rabbitmq:
                            image: rabbitmq:4-management
                            hostname: rabbitmq
                            environment:
                              RABBITMQ_DEFAULT_USER: {{Escape(inputs["username"])}}
                              RABBITMQ_DEFAULT_PASS: {{Escape(inputs["password"])}}
                            volumes:
                              - rabbitmq-data:/var/lib/rabbitmq
                            ports:
                              - "5672:5672"
                              - "15672:15672"
                            healthcheck:
                              test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
                              interval: 5s
                              timeout: 30s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          rabbitmq-data:
                        """),
        new(
            "portainer",
            "Portainer",
            "Docker management UI.",
            "1",
            [],
            _ => """
                 services:
                   portainer:
                     image: portainer/portainer-ce:alpine
                     volumes:
                       - /var/run/docker.sock:/var/run/docker.sock
                       - portainer-data:/data
                     ports:
                       - "9443:9443"
                       - "9000:9000"
                     healthcheck:
                       test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:9000/ || wget --no-check-certificate -qO- https://127.0.0.1:9443/"]
                       interval: 20s
                       timeout: 20s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   portainer-data:
                 """),
        new(
            "open-webui",
            "Open WebUI",
            "Self-hosted AI chat interface for Ollama and OpenAI-compatible APIs.",
            "1",
            [
                new ServiceTemplateInput("openAiApiKey", "OpenAI API key", false, true, null),
                new ServiceTemplateInput("ollamaBaseUrl", "Ollama base URL", false, false, null)
            ],
            inputs => $$"""
                        services:
                          open-webui:
                            image: ghcr.io/open-webui/open-webui:main
                            environment:
                              OPENAI_API_KEY: {{Escape(Optional(inputs, "openAiApiKey"))}}
                              OLLAMA_BASE_URL: {{Escape(Optional(inputs, "ollamaBaseUrl"))}}
                            volumes:
                              - open-webui-data:/app/backend/data
                            ports:
                              - "3000:8080"
                            extra_hosts:
                              - host.docker.internal:host-gateway
                            healthcheck:
                              test: ["CMD", "curl", "-f", "http://127.0.0.1:8080"]
                              interval: 5s
                              timeout: 30s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          open-webui-data:
                        """),
        new(
            "openclaw",
            "OpenClaw",
            "AI-powered coding assistant with browser automation.",
            "1",
            [
                new ServiceTemplateInput("username", "Username", true, false, "openclaw"),
                new ServiceTemplateInput("password", "Password", true, true, null),
                new ServiceTemplateInput("gatewayToken", "Gateway token", true, true, null),
                new ServiceTemplateInput("openAiApiKey", "OpenAI API key", false, true, null),
                new ServiceTemplateInput("anthropicApiKey", "Anthropic API key", false, true, null)
            ],
            inputs => $$"""
                        services:
                          openclaw:
                            image: coollabsio/openclaw:2026.2.6
                            environment:
                              AUTH_USERNAME: {{Escape(inputs["username"])}}
                              AUTH_PASSWORD: {{Escape(inputs["password"])}}
                              OPENCLAW_GATEWAY_TOKEN: {{Escape(inputs["gatewayToken"])}}
                              OPENAI_API_KEY: {{Escape(Optional(inputs, "openAiApiKey"))}}
                              ANTHROPIC_API_KEY: {{Escape(Optional(inputs, "anthropicApiKey"))}}
                              PORT: "8080"
                              OPENCLAW_GATEWAY_PORT: "18789"
                              OPENCLAW_GATEWAY_BIND: loopback
                              OPENCLAW_STATE_DIR: /data/.openclaw
                              OPENCLAW_WORKSPACE_DIR: /data/workspace
                              BROWSER_CDP_URL: http://browser:9223
                              BROWSER_DEFAULT_PROFILE: openclaw
                              BROWSER_EVALUATE_ENABLED: "true"
                            volumes:
                              - openclaw-data:/data
                            ports:
                              - "8083:8080"
                            depends_on:
                              browser:
                                condition: service_healthy
                            healthcheck:
                              test: ["CMD", "curl", "-sf", "http://127.0.0.1:8080/healthz"]
                              interval: 10s
                              timeout: 10s
                              retries: 5
                            restart: unless-stopped
                          browser:
                            image: coollabsio/openclaw-browser:latest
                            environment:
                              PUID: "1000"
                              PGID: "1000"
                              TZ: Etc/UTC
                              CHROME_CLI: --remote-debugging-port=9222
                            volumes:
                              - openclaw-browser-data:/config
                            shm_size: 2g
                            healthcheck:
                              test: ["CMD-SHELL", "bash -c ':> /dev/tcp/127.0.0.1/9222' || exit 1"]
                              interval: 5s
                              timeout: 5s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          openclaw-data:
                          openclaw-browser-data:
                        """),
        new(
            "n8n",
            "n8n",
            "Workflow automation platform.",
            "1",
            [
                new ServiceTemplateInput("baseUrl", "Base URL", true, false, "http://localhost:5678"),
                new ServiceTemplateInput("encryptionKey", "Encryption key", true, true, null),
                new ServiceTemplateInput("timezone", "Timezone", true, false, "UTC")
            ],
            inputs => $$"""
                        services:
                          n8n:
                            image: n8nio/n8n:latest
                            environment:
                              N8N_PORT: "5678"
                              N8N_HOST: 0.0.0.0
                              N8N_PROTOCOL: http
                              N8N_EDITOR_BASE_URL: {{Escape(inputs["baseUrl"])}}
                              WEBHOOK_URL: {{Escape(inputs["baseUrl"])}}
                              N8N_ENCRYPTION_KEY: {{Escape(inputs["encryptionKey"])}}
                              GENERIC_TIMEZONE: {{Escape(inputs["timezone"])}}
                              TZ: {{Escape(inputs["timezone"])}}
                              N8N_ENFORCE_SETTINGS_FILE_PERMISSIONS: "true"
                              N8N_RUNNERS_ENABLED: "true"
                            volumes:
                              - n8n-data:/home/node/.n8n
                            ports:
                              - "5678:5678"
                            healthcheck:
                              test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:5678/healthz"]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          n8n-data:
                        """),
        new(
            "excalidraw",
            "Excalidraw",
            "Virtual whiteboard for hand-drawn style diagrams.",
            "1",
            [],
            _ => """
                 services:
                   excalidraw:
                     image: excalidraw/excalidraw:latest
                     ports:
                       - "8084:80"
                     healthcheck:
                       test: ["CMD", "wget", "--spider", "--quiet", "http://localhost"]
                       interval: 10s
                       timeout: 5s
                       retries: 10
                     restart: unless-stopped
                 """),
        new(
            "gitlab",
            "GitLab",
            "Self-hosted GitLab Community Edition DevOps platform.",
            "1",
            [
                new ServiceTemplateInput("externalUrl", "External URL", true, false, "http://localhost"),
                new ServiceTemplateInput("rootPassword", "Root password", true, true, null),
                new ServiceTemplateInput("timezone", "Timezone", true, false, "UTC"),
                new ServiceTemplateInput("sshPort", "SSH host port", true, false, "2222")
            ],
            inputs => $$"""
                        services:
                          gitlab:
                            image: gitlab/gitlab-ce:latest
                            environment:
                              TZ: {{Escape(inputs["timezone"])}}
                              GITLAB_TIMEZONE: {{Escape(inputs["timezone"])}}
                              GITLAB_ROOT_PASSWORD: {{Escape(inputs["rootPassword"])}}
                              EXTERNAL_URL: {{Escape(inputs["externalUrl"])}}
                              GITLAB_OMNIBUS_CONFIG: external_url '{{Escape(inputs["externalUrl"])}}'; nginx['listen_https'] = false; nginx['listen_port'] = 80; gitlab_rails['gitlab_shell_ssh_port'] = {{Escape(inputs["sshPort"])}};
                            volumes:
                              - gitlab-config:/etc/gitlab
                              - gitlab-logs:/var/log/gitlab
                              - gitlab-data:/var/opt/gitlab
                            ports:
                              - "8085:80"
                              - "{{Escape(inputs["sshPort"])}}:22"
                            shm_size: 256m
                            healthcheck:
                              test: ["CMD-SHELL", "curl -f http://127.0.0.1/-/health || exit 1"]
                              interval: 30s
                              timeout: 10s
                              retries: 20
                            restart: unless-stopped
                        volumes:
                          gitlab-config:
                          gitlab-logs:
                          gitlab-data:
                        """),
        new(
            "gitea",
            "Gitea",
            "Lightweight self-hosted Git service.",
            "1",
            [
                new ServiceTemplateInput("userUid", "User UID", true, false, "1000"),
                new ServiceTemplateInput("userGid", "User GID", true, false, "1000"),
                new ServiceTemplateInput("sshPort", "SSH host port", true, false, "22222")
            ],
            inputs => $$"""
                        services:
                          gitea:
                            image: gitea/gitea:latest
                            environment:
                              USER_UID: {{Escape(inputs["userUid"])}}
                              USER_GID: {{Escape(inputs["userGid"])}}
                            volumes:
                              - gitea-data:/data
                            ports:
                              - "3003:3000"
                              - "{{Escape(inputs["sshPort"])}}:22"
                            healthcheck:
                              test: ["CMD", "curl", "-f", "http://127.0.0.1:3000"]
                              interval: 2s
                              timeout: 10s
                              retries: 15
                            restart: unless-stopped
                        volumes:
                          gitea-data:
                        """),
        new(
            "gitea-mariadb",
            "Gitea with MariaDB",
            "Gitea backed by a bundled MariaDB database.",
            "1",
            GiteaDatabaseInputs(),
            inputs => GiteaWithDatabaseCompose("mariadb", "mariadb:11", "mysql", "gitea-mariadb-data", inputs)),
        new(
            "gitea-postgres",
            "Gitea with Postgres",
            "Gitea backed by a bundled PostgreSQL database.",
            "1",
            GiteaDatabaseInputs(),
            inputs => GiteaWithDatabaseCompose("postgresql", "postgres:16-alpine", "postgres", "gitea-postgresql-data",
                inputs)),
        new(
            "gitea-mysql",
            "Gitea with MySQL",
            "Gitea backed by a bundled MySQL database.",
            "1",
            GiteaDatabaseInputs(),
            inputs => GiteaWithDatabaseCompose("mysql", "mysql:8.0", "mysql", "gitea-mysql-data", inputs)),
        new(
            "grafana",
            "Grafana",
            "Open-source analytics and monitoring dashboards.",
            "1",
            [
                new ServiceTemplateInput("adminPassword", "Admin password", true, true, null),
                new ServiceTemplateInput("rootUrl", "Root URL", false, false, null)
            ],
            inputs => $$"""
                        services:
                          grafana:
                            image: grafana/grafana-oss:latest
                            environment:
                              GF_SECURITY_ADMIN_PASSWORD: {{Escape(inputs["adminPassword"])}}
                              GF_SERVER_ROOT_URL: {{Escape(Optional(inputs, "rootUrl"))}}
                            volumes:
                              - grafana-data:/var/lib/grafana
                            ports:
                              - "3004:3000"
                            healthcheck:
                              test: ["CMD", "curl", "-f", "http://127.0.0.1:3000/api/health"]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          grafana-data:
                        """),
        new(
            "grafana-postgres",
            "Grafana with Postgres",
            "Grafana backed by a bundled PostgreSQL database.",
            "1",
            [
                new ServiceTemplateInput("adminPassword", "Admin password", true, true, null),
                new ServiceTemplateInput("database", "Database", true, false, "grafana"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "grafana"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null),
                new ServiceTemplateInput("rootUrl", "Root URL", false, false, null)
            ],
            inputs => $$"""
                        services:
                          grafana:
                            image: grafana/grafana-oss:latest
                            environment:
                              GF_SECURITY_ADMIN_PASSWORD: {{Escape(inputs["adminPassword"])}}
                              GF_SERVER_ROOT_URL: {{Escape(Optional(inputs, "rootUrl"))}}
                              GF_DATABASE_TYPE: postgres
                              GF_DATABASE_HOST: grafana-postgresql:5432
                              GF_DATABASE_NAME: {{Escape(inputs["database"])}}
                              GF_DATABASE_USER: {{Escape(inputs["databaseUser"])}}
                              GF_DATABASE_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - grafana-data:/var/lib/grafana
                            ports:
                              - "3004:3000"
                            depends_on:
                              grafana-postgresql:
                                condition: service_healthy
                            healthcheck:
                              test: ["CMD", "curl", "-f", "http://127.0.0.1:3000/api/health"]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                          grafana-postgresql:
                            image: postgres:16-alpine
                            environment:
                              POSTGRES_DB: {{Escape(inputs["database"])}}
                              POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                              POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - grafana-postgresql-data:/var/lib/postgresql/data
                            healthcheck:
                              test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          grafana-data:
                          grafana-postgresql-data:
                        """),
        new(
            "home-assistant",
            "Home Assistant",
            "Local-first home automation platform.",
            "1",
            [new ServiceTemplateInput("timezone", "Timezone", true, false, "UTC")],
            inputs => $$"""
                        services:
                          homeassistant:
                            image: ghcr.io/home-assistant/home-assistant:stable
                            environment:
                              TZ: {{Escape(inputs["timezone"])}}
                              DISABLE_JEMALLOC: "false"
                            volumes:
                              - homeassistant-config:/config
                              - /run/dbus:/run/dbus:ro
                            ports:
                              - "8123:8123"
                            privileged: true
                            healthcheck:
                              test: ["CMD", "curl", "-f", "http://localhost:8123"]
                              interval: 30s
                              timeout: 10s
                              retries: 3
                              start_period: 60s
                            restart: unless-stopped
                        volumes:
                          homeassistant-config:
                        """),
        new(
            "minecraft-server",
            "Minecraft Server",
            "Java Edition Minecraft server with persistent world data.",
            "1",
            [
                new ServiceTemplateInput("motd", "Message of the day", true, false, "Vessel Minecraft"),
                new ServiceTemplateInput("serverType", "Server type", true, false, "VANILLA"),
                new ServiceTemplateInput("minecraftVersion", "Minecraft version", true, false, "LATEST"),
                new ServiceTemplateInput("memory", "Memory", true, false, "2G"),
                new ServiceTemplateInput("maxPlayers", "Max players", true, false, "20"),
                new ServiceTemplateInput("rconPassword", "RCON password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          minecraft:
                            image: itzg/minecraft-server:latest
                            environment:
                              EULA: "TRUE"
                              TYPE: {{Escape(inputs["serverType"])}}
                              VERSION: {{Escape(inputs["minecraftVersion"])}}
                              MEMORY: {{Escape(inputs["memory"])}}
                              MOTD: {{Escape(inputs["motd"])}}
                              MAX_PLAYERS: {{Escape(inputs["maxPlayers"])}}
                              ENABLE_RCON: "true"
                              RCON_PASSWORD: {{Escape(inputs["rconPassword"])}}
                            volumes:
                              - minecraft-data:/data
                            ports:
                              - "25565:25565"
                            healthcheck:
                              test: ["CMD", "mc-health"]
                              interval: 30s
                              timeout: 10s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          minecraft-data:
                        """),
        new(
            "moodle",
            "Moodle",
            "Open-source learning management system with a bundled MariaDB database.",
            "1",
            [
                new ServiceTemplateInput("adminUsername", "Admin username", true, false, "admin"),
                new ServiceTemplateInput("adminPassword", "Admin password", true, true, null),
                new ServiceTemplateInput("adminEmail", "Admin email", true, false, "admin@example.com"),
                new ServiceTemplateInput("siteName", "Site name", true, false, "Vessel Moodle"),
                new ServiceTemplateInput("database", "Database", true, false, "moodle"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "moodle"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null),
                new ServiceTemplateInput("rootPassword", "Database root password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          moodle:
                            image: bitnami/moodle:latest
                            environment:
                              MOODLE_USERNAME: {{Escape(inputs["adminUsername"])}}
                              MOODLE_PASSWORD: {{Escape(inputs["adminPassword"])}}
                              MOODLE_EMAIL: {{Escape(inputs["adminEmail"])}}
                              MOODLE_SITE_NAME: {{Escape(inputs["siteName"])}}
                              MOODLE_DATABASE_TYPE: mariadb
                              MOODLE_DATABASE_HOST: moodle-mariadb
                              MOODLE_DATABASE_PORT_NUMBER: "3306"
                              MOODLE_DATABASE_NAME: {{Escape(inputs["database"])}}
                              MOODLE_DATABASE_USER: {{Escape(inputs["databaseUser"])}}
                              MOODLE_DATABASE_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - moodle-data:/bitnami/moodle
                              - moodledata-data:/bitnami/moodledata
                            ports:
                              - "8086:8080"
                            depends_on:
                              moodle-mariadb:
                                condition: service_healthy
                            healthcheck:
                              test: ["CMD-SHELL", "curl -f http://127.0.0.1:8080/login/index.php || exit 1"]
                              interval: 10s
                              timeout: 20s
                              retries: 20
                            restart: unless-stopped
                          moodle-mariadb:
                            image: bitnami/mariadb:latest
                            environment:
                              MARIADB_ROOT_PASSWORD: {{Escape(inputs["rootPassword"])}}
                              MARIADB_DATABASE: {{Escape(inputs["database"])}}
                              MARIADB_USER: {{Escape(inputs["databaseUser"])}}
                              MARIADB_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - moodle-mariadb-data:/bitnami/mariadb
                            healthcheck:
                              test: ["CMD-SHELL", "mariadb-admin ping -h 127.0.0.1 -u root -p\"$${MARIADB_ROOT_PASSWORD}\""]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          moodle-data:
                          moodledata-data:
                          moodle-mariadb-data:
                        """),
        new(
            "matrix-synapse-postgres",
            "Matrix Synapse with Postgres",
            "Matrix homeserver backed by a bundled PostgreSQL database.",
            "1",
            [
                new ServiceTemplateInput("serverName", "Server name", true, false, "matrix.example.com"),
                new ServiceTemplateInput("reportStats", "Report anonymous stats", true, false, "no"),
                new ServiceTemplateInput("database", "Database", true, false, "synapse"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "synapse"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null)
            ],
            inputs => MatrixSynapseCompose("postgres", inputs)),
        new(
            "matrix-synapse-sqlite",
            "Matrix Synapse with SQLite",
            "Matrix homeserver using its embedded SQLite database.",
            "1",
            [
                new ServiceTemplateInput("serverName", "Server name", true, false, "matrix.example.com"),
                new ServiceTemplateInput("reportStats", "Report anonymous stats", true, false, "no")
            ],
            inputs => MatrixSynapseCompose("sqlite", inputs)),
        new(
            "marimo",
            "Marimo",
            "Reactive Python notebook workspace.",
            "1",
            [
                new ServiceTemplateInput("password", "Password", true, true, null),
                new ServiceTemplateInput("baseUrl", "Base URL", false, false, null)
            ],
            inputs => $$"""
                        services:
                          marimo:
                            image: ghcr.io/marimo-team/marimo:latest
                            command: ["marimo", "edit", "--host", "0.0.0.0", "--port", "2718", "--headless", "--no-token", "/workspace"]
                            environment:
                              MARIMO_PASSWORD: {{Escape(inputs["password"])}}
                              MARIMO_BASE_URL: {{Escape(Optional(inputs, "baseUrl"))}}
                            volumes:
                              - marimo-workspace:/workspace
                            ports:
                              - "2718:2718"
                            healthcheck:
                              test: ["CMD", "python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:2718', timeout=5)"]
                              interval: 10s
                              timeout: 10s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          marimo-workspace:
                        """),
        new(
            "jupyter-notebook-python",
            "Jupyter Notebook Python",
            "Python notebook workspace using the official Jupyter Docker Stack.",
            "1",
            [
                new ServiceTemplateInput("token", "Access token", true, true, null),
                new ServiceTemplateInput("timezone", "Timezone", true, false, "UTC")
            ],
            inputs => $$"""
                        services:
                          jupyter-notebook-python:
                            image: quay.io/jupyter/base-notebook:latest
                            environment:
                              JUPYTER_TOKEN: {{Escape(inputs["token"])}}
                              TZ: {{Escape(inputs["timezone"])}}
                            volumes:
                              - jupyter-notebook-python-work:/home/jovyan/work
                            ports:
                              - "8888:8888"
                            healthcheck:
                              test: ["CMD", "wget", "-q", "--spider", "http://127.0.0.1:8888/api"]
                              interval: 10s
                              timeout: 10s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          jupyter-notebook-python-work:
                        """),
        new(
            "drizzle-gateway",
            "Drizzle Gateway",
            "Web gateway for browsing and managing Drizzle-connected databases.",
            "1",
            [
                new ServiceTemplateInput("masterPassword", "Master password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          drizzle-gateway:
                            image: ghcr.io/drizzle-team/gateway:latest
                            environment:
                              MASTER_PASSWORD: {{Escape(inputs["masterPassword"])}}
                              STORE_PATH: /app
                            volumes:
                              - drizzle-gateway-data:/app
                            ports:
                              - "4983:4983"
                            restart: unless-stopped
                        volumes:
                          drizzle-gateway-data:
                        """),
        new(
            "drupal-postgres",
            "Drupal with PostgreSQL",
            "Drupal CMS backed by a bundled PostgreSQL database.",
            "1",
            [
                new ServiceTemplateInput("database", "Database", true, false, "drupal"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "drupal"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null)
            ],
            inputs => DrupalWithPostgresCompose(inputs)),
        new(
            "electricsql",
            "ElectricSQL",
            "Postgres sync service for local-first applications.",
            "1",
            [
                new ServiceTemplateInput("databaseUrl", "Postgres database URL", true, true, null),
                new ServiceTemplateInput("electricSecret", "Electric secret", true, true, null)
            ],
            inputs => $$"""
                        services:
                          electricsql:
                            image: electricsql/electric:latest
                            environment:
                              DATABASE_URL: {{Escape(inputs["databaseUrl"])}}
                              ELECTRIC_SECRET: {{Escape(inputs["electricSecret"])}}
                            ports:
                              - "5133:3000"
                            healthcheck:
                              test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:3000/v1/health || exit 1"]
                              interval: 10s
                              timeout: 10s
                              retries: 10
                            restart: unless-stopped
                        """),
        new(
            "codimd",
            "CodiMD",
            "Collaborative Markdown notes backed by PostgreSQL.",
            "1",
            [
                new ServiceTemplateInput("domain", "Domain", true, false, "localhost"),
                new ServiceTemplateInput("database", "Database", true, false, "codimd"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "codimd"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null),
                new ServiceTemplateInput("sessionSecret", "Session secret", true, true, null)
            ],
            inputs => $$"""
                        services:
                          codimd:
                            image: quay.io/hedgedoc/hedgedoc:latest
                            environment:
                              CMD_DB_URL: postgres://{{Escape(inputs["databaseUser"])}}:{{Escape(inputs["databasePassword"])}}@codimd-postgresql:5432/{{Escape(inputs["database"])}}
                              CMD_DOMAIN: {{Escape(inputs["domain"])}}
                              CMD_PROTOCOL_USESSL: "false"
                              CMD_URL_ADDPORT: "true"
                              CMD_SESSION_SECRET: {{Escape(inputs["sessionSecret"])}}
                            volumes:
                              - codimd-uploads:/hedgedoc/public/uploads
                            ports:
                              - "3006:3000"
                            depends_on:
                              codimd-postgresql:
                                condition: service_healthy
                            healthcheck:
                              test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:3000/status || exit 1"]
                              interval: 10s
                              timeout: 10s
                              retries: 20
                            restart: unless-stopped
                          codimd-postgresql:
                            image: postgres:16-alpine
                            environment:
                              POSTGRES_DB: {{Escape(inputs["database"])}}
                              POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                              POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - codimd-postgresql-data:/var/lib/postgresql/data
                            healthcheck:
                              test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          codimd-uploads:
                          codimd-postgresql-data:
                        """),
        new(
            "librechat",
            "LibreChat",
            "AI chat platform with MongoDB and Meilisearch.",
            "1",
            [
                new ServiceTemplateInput("jwtSecret", "JWT secret", true, true, null),
                new ServiceTemplateInput("jwtRefreshSecret", "JWT refresh secret", true, true, null),
                new ServiceTemplateInput("credsKey", "Credentials key", true, true, null),
                new ServiceTemplateInput("credsIv", "Credentials IV", true, true, null),
                new ServiceTemplateInput("meiliMasterKey", "Meilisearch master key", true, true, null)
            ],
            inputs => $$"""
                        services:
                          librechat:
                            image: ghcr.io/danny-avila/librechat:latest
                            environment:
                              HOST: 0.0.0.0
                              PORT: "3080"
                              MONGO_URI: mongodb://librechat-mongodb:27017/LibreChat
                              MEILI_HOST: http://librechat-meilisearch:7700
                              MEILI_MASTER_KEY: {{Escape(inputs["meiliMasterKey"])}}
                              JWT_SECRET: {{Escape(inputs["jwtSecret"])}}
                              JWT_REFRESH_SECRET: {{Escape(inputs["jwtRefreshSecret"])}}
                              CREDS_KEY: {{Escape(inputs["credsKey"])}}
                              CREDS_IV: {{Escape(inputs["credsIv"])}}
                            volumes:
                              - librechat-images:/app/client/public/images
                              - librechat-logs:/app/api/logs
                            ports:
                              - "3080:3080"
                            depends_on:
                              librechat-mongodb:
                                condition: service_started
                              librechat-meilisearch:
                                condition: service_healthy
                            restart: unless-stopped
                          librechat-mongodb:
                            image: mongo:7
                            volumes:
                              - librechat-mongodb-data:/data/db
                            restart: unless-stopped
                          librechat-meilisearch:
                            image: getmeili/meilisearch:v1.7
                            environment:
                              MEILI_MASTER_KEY: {{Escape(inputs["meiliMasterKey"])}}
                              MEILI_NO_ANALYTICS: "true"
                            volumes:
                              - librechat-meilisearch-data:/meili_data
                            healthcheck:
                              test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:7700/health || exit 1"]
                              interval: 10s
                              timeout: 10s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          librechat-images:
                          librechat-logs:
                          librechat-mongodb-data:
                          librechat-meilisearch-data:
                        """),
        new(
            "mailpit",
            "Mailpit",
            "Email testing SMTP server and web UI.",
            "1",
            [],
            _ => """
                 services:
                   mailpit:
                     image: axllent/mailpit:latest
                     ports:
                       - "8025:8025"
                       - "1025:1025"
                     healthcheck:
                       test: ["CMD", "wget", "-q", "--spider", "http://127.0.0.1:8025"]
                       interval: 10s
                       timeout: 10s
                       retries: 10
                     restart: unless-stopped
                 """),
        new(
            "nextcloud",
            "Nextcloud",
            "File sync and collaboration platform using embedded SQLite.",
            "1",
            NextcloudInputs(false),
            inputs => NextcloudCompose(null, inputs)),
        new(
            "nextcloud-postgres",
            "Nextcloud with Postgres",
            "Nextcloud backed by a bundled PostgreSQL database.",
            "1",
            NextcloudInputs(true),
            inputs => NextcloudCompose("postgres", inputs)),
        new(
            "nextcloud-mysql",
            "Nextcloud with MySQL",
            "Nextcloud backed by a bundled MySQL database.",
            "1",
            NextcloudInputs(true, true),
            inputs => NextcloudCompose("mysql", inputs)),
        new(
            "nextcloud-mariadb",
            "Nextcloud with MariaDB",
            "Nextcloud backed by a bundled MariaDB database.",
            "1",
            NextcloudInputs(true, true),
            inputs => NextcloudCompose("mariadb", inputs)),
        new(
            "odoo",
            "Odoo",
            "Business application suite backed by PostgreSQL.",
            "1",
            [
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "odoo"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          odoo:
                            image: odoo:latest
                            environment:
                              HOST: odoo-postgresql
                              USER: {{Escape(inputs["databaseUser"])}}
                              PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - odoo-data:/var/lib/odoo
                              - odoo-config:/etc/odoo
                            ports:
                              - "8069:8069"
                            depends_on:
                              odoo-postgresql:
                                condition: service_healthy
                            restart: unless-stopped
                          odoo-postgresql:
                            image: postgres:16-alpine
                            environment:
                              POSTGRES_DB: postgres
                              POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                              POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - odoo-postgresql-data:/var/lib/postgresql/data
                            healthcheck:
                              test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d postgres"]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          odoo-data:
                          odoo-config:
                          odoo-postgresql-data:
                        """),
        new(
            "pocketbase",
            "PocketBase",
            "Single-file backend with realtime database, auth, and file storage.",
            "1",
            [],
            _ => """
                 services:
                   pocketbase:
                     image: ghcr.io/muchobien/pocketbase:latest
                     volumes:
                       - pocketbase-data:/pb_data
                       - pocketbase-public:/pb_public
                     ports:
                       - "8090:8090"
                     healthcheck:
                       test: ["CMD", "wget", "-q", "--spider", "http://127.0.0.1:8090/api/health"]
                       interval: 10s
                       timeout: 10s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   pocketbase-data:
                   pocketbase-public:
                 """),
        new(
            "wikijs",
            "Wiki.js",
            "Modern wiki platform backed by PostgreSQL.",
            "1",
            [
                new ServiceTemplateInput("database", "Database", true, false, "wiki"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "wiki"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          wikijs:
                            image: requarks/wiki:2
                            environment:
                              DB_TYPE: postgres
                              DB_HOST: wikijs-postgresql
                              DB_PORT: "5432"
                              DB_USER: {{Escape(inputs["databaseUser"])}}
                              DB_PASS: {{Escape(inputs["databasePassword"])}}
                              DB_NAME: {{Escape(inputs["database"])}}
                            ports:
                              - "3007:3000"
                            depends_on:
                              wikijs-postgresql:
                                condition: service_healthy
                            healthcheck:
                              test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:3000/healthz || exit 1"]
                              interval: 10s
                              timeout: 10s
                              retries: 20
                            restart: unless-stopped
                          wikijs-postgresql:
                            image: postgres:16-alpine
                            environment:
                              POSTGRES_DB: {{Escape(inputs["database"])}}
                              POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                              POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - wikijs-postgresql-data:/var/lib/postgresql/data
                            healthcheck:
                              test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          wikijs-postgresql-data:
                        """),
        new(
            "keycloak",
            "Keycloak",
            "Open-source identity and access management server.",
            "1",
            [
                new ServiceTemplateInput("adminUsername", "Admin username", true, false, "admin"),
                new ServiceTemplateInput("adminPassword", "Admin password", true, true, null),
                new ServiceTemplateInput("hostname", "Hostname URL", false, false, null),
                new ServiceTemplateInput("timezone", "Timezone", true, false, "UTC")
            ],
            inputs => KeycloakCompose(null, inputs)),
        new(
            "keycloak-postgres",
            "Keycloak with Postgres",
            "Keycloak backed by a bundled PostgreSQL database.",
            "1",
            [
                new ServiceTemplateInput("adminUsername", "Admin username", true, false, "admin"),
                new ServiceTemplateInput("adminPassword", "Admin password", true, true, null),
                new ServiceTemplateInput("hostname", "Hostname URL", false, false, null),
                new ServiceTemplateInput("timezone", "Timezone", true, false, "UTC"),
                new ServiceTemplateInput("database", "Database", true, false, "keycloak"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "keycloak"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null)
            ],
            inputs => KeycloakCompose("postgres", inputs)),
        new(
            "cloudflared",
            "Cloudflared",
            "Cloudflare Tunnel connector for exposing services through Cloudflare.",
            "1",
            [new ServiceTemplateInput("token", "Tunnel token", true, true, null)],
            inputs => $$"""
                        services:
                          cloudflared:
                            image: cloudflare/cloudflared:latest
                            command: tunnel --no-autoupdate run --token {{Escape(inputs["token"])}}
                            restart: unless-stopped
                        """),
        new(
            "metabase",
            "Metabase",
            "Business intelligence and analytics dashboard.",
            "1",
            [
                new ServiceTemplateInput("encryptionKey", "Encryption key", true, true, null),
                new ServiceTemplateInput("siteUrl", "Site URL", false, false, null)
            ],
            inputs => $$"""
                        services:
                          metabase:
                            image: metabase/metabase:latest
                            environment:
                              MB_ENCRYPTION_SECRET_KEY: {{Escape(inputs["encryptionKey"])}}
                              MB_SITE_URL: {{Escape(Optional(inputs, "siteUrl"))}}
                            volumes:
                              - metabase-data:/metabase-data
                            ports:
                              - "3005:3000"
                            healthcheck:
                              test: ["CMD", "curl", "-f", "http://127.0.0.1:3000/api/health"]
                              interval: 10s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          metabase-data:
                        """),
        new(
            "minio",
            "MinIO",
            "S3-compatible object storage service.",
            "1",
            [
                new ServiceTemplateInput("rootUser", "Root user", true, false, "minioadmin"),
                new ServiceTemplateInput("rootPassword", "Root password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          minio:
                            image: quay.io/minio/minio:latest
                            command: server /data --console-address ":9001"
                            environment:
                              MINIO_ROOT_USER: {{Escape(inputs["rootUser"])}}
                              MINIO_ROOT_PASSWORD: {{Escape(inputs["rootPassword"])}}
                            volumes:
                              - minio-data:/data
                            ports:
                              - "9000:9000"
                              - "9001:9001"
                            restart: unless-stopped
                        volumes:
                          minio-data:
                        """),
        new(
            "clickhouse",
            "ClickHouse DB",
            "Column-oriented OLAP database for realtime analytics.",
            "1",
            [
                new ServiceTemplateInput("database", "Database", true, false, "default"),
                new ServiceTemplateInput("username", "Username", true, false, "default"),
                new ServiceTemplateInput("password", "Password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          clickhouse:
                            image: clickhouse/clickhouse-server:25.11
                            environment:
                              CLICKHOUSE_DB: {{Escape(inputs["database"])}}
                              CLICKHOUSE_USER: {{Escape(inputs["username"])}}
                              CLICKHOUSE_PASSWORD: {{Escape(inputs["password"])}}
                              CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT: "1"
                            ulimits:
                              nofile:
                                soft: 262144
                                hard: 262144
                            volumes:
                              - clickhouse-data:/var/lib/clickhouse
                            ports:
                              - "8123:8123"
                              - "9000:9000"
                            healthcheck:
                              test: ["CMD-SHELL", "clickhouse-client --user \"$${CLICKHOUSE_USER}\" --password \"$${CLICKHOUSE_PASSWORD}\" --query 'SELECT 1'"]
                              interval: 5s
                              timeout: 5s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          clickhouse-data:
                        """),
        new(
            "tigerbeetle",
            "TigerBeetle DB",
            "Financial transactions database for double-entry ledger workloads.",
            "1",
            [
                new ServiceTemplateInput("clusterId", "Cluster ID", true, false, "0"),
                new ServiceTemplateInput("replicaIndex", "Replica index", true, false, "0"),
                new ServiceTemplateInput("replicaCount", "Replica count", true, false, "1"),
                new ServiceTemplateInput("address", "Listen address", true, false, "0.0.0.0:3000")
            ],
            inputs => $$"""
                        services:
                          tigerbeetle-format:
                            image: ghcr.io/tigerbeetle/tigerbeetle:latest
                            command: format --cluster={{Escape(inputs["clusterId"])}} --replica={{Escape(inputs["replicaIndex"])}} --replica-count={{Escape(inputs["replicaCount"])}} /data/{{Escape(inputs["clusterId"])}}_{{Escape(inputs["replicaIndex"])}}.tigerbeetle
                            volumes:
                              - tigerbeetle-data:/data
                            security_opt:
                              - seccomp=unconfined
                            restart: "no"
                          tigerbeetle:
                            image: ghcr.io/tigerbeetle/tigerbeetle:latest
                            command: start --addresses={{Escape(inputs["address"])}} /data/{{Escape(inputs["clusterId"])}}_{{Escape(inputs["replicaIndex"])}}.tigerbeetle
                            depends_on:
                              tigerbeetle-format:
                                condition: service_completed_successfully
                            volumes:
                              - tigerbeetle-data:/data
                            security_opt:
                              - seccomp=unconfined
                            ports:
                              - "3000:3000"
                            restart: unless-stopped
                        volumes:
                          tigerbeetle-data:
                        """),
        new(
            "rustfs",
            "RustFS",
            "S3-compatible object storage service built with Rust.",
            "1",
            [
                new ServiceTemplateInput("accessKey", "Access key", true, false, "rustfsadmin"),
                new ServiceTemplateInput("secretKey", "Secret key", true, true, null)
            ],
            inputs => $$"""
                        services:
                          rustfs:
                            image: rustfs/rustfs:latest
                            command: /data
                            environment:
                              RUSTFS_ADDRESS: 0.0.0.0:9000
                              RUSTFS_CONSOLE_ADDRESS: 0.0.0.0:9001
                              RUSTFS_ACCESS_KEY: {{Escape(inputs["accessKey"])}}
                              RUSTFS_SECRET_KEY: {{Escape(inputs["secretKey"])}}
                              RUSTFS_CONSOLE_ENABLE: "true"
                              RUSTFS_CORS_ALLOWED_ORIGINS: "*"
                              RUSTFS_CONSOLE_CORS_ALLOWED_ORIGINS: "*"
                            volumes:
                              - rustfs-data:/data
                            ports:
                              - "9000:9000"
                              - "9001:9001"
                            healthcheck:
                              test: ["CMD-SHELL", "curl -f http://127.0.0.1:9000/health && curl -f http://127.0.0.1:9001/rustfs/console/health"]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          rustfs-data:
                        """),
        new(
            "postgres-postgis",
            "Postgres (with PostGIS)",
            "PostgreSQL database with PostGIS spatial extensions preinstalled.",
            "1",
            [
                new ServiceTemplateInput("database", "Database", true, false, "postgres"),
                new ServiceTemplateInput("username", "Username", true, false, "postgres"),
                new ServiceTemplateInput("password", "Password", true, true, null)
            ],
            inputs => PostgresCompose(
                "postgis",
                "postgis/postgis:17-3.5",
                "postgis-data",
                inputs)),
        new(
            "postgres-timescale",
            "Postgres (with Timescale DB)",
            "PostgreSQL database with TimescaleDB extensions preinstalled.",
            "1",
            [
                new ServiceTemplateInput("database", "Database", true, false, "postgres"),
                new ServiceTemplateInput("username", "Username", true, false, "postgres"),
                new ServiceTemplateInput("password", "Password", true, true, null)
            ],
            inputs => PostgresCompose(
                "timescaledb",
                "timescale/timescaledb:latest-pg17",
                "timescaledb-data",
                inputs)),
        new(
            "databasus",
            "Databasus",
            "Self-hosted database backup tool for PostgreSQL, MySQL, MariaDB, and MongoDB.",
            "1",
            [],
            _ => """
                 services:
                   databasus:
                     image: databasus/databasus:latest
                     volumes:
                       - databasus-data:/databasus-data
                     ports:
                       - "4005:4005"
                     healthcheck:
                       test: ["CMD", "wget", "-qO-", "http://localhost:4005/api/v1/system/health"]
                       interval: 5s
                       timeout: 10s
                       retries: 5
                     restart: unless-stopped
                 volumes:
                   databasus-data:
                 """),
        new(
            "valkey",
            "Valkey",
            "Redis-compatible in-memory data store.",
            "1",
            [new ServiceTemplateInput("password", "Password", true, true, null)],
            inputs => RedisCompatibleStoreCompose(
                "valkey",
                "valkey/valkey:latest",
                "valkey-server",
                "valkey-cli",
                "valkey-data",
                "VALKEY_PASSWORD",
                inputs)),
        new(
            "keydb",
            "KeyDB",
            "Multithreaded Redis-compatible database.",
            "1",
            [new ServiceTemplateInput("password", "Password", true, true, null)],
            inputs => RedisCompatibleStoreCompose(
                "keydb",
                "eqalpha/keydb:latest",
                "keydb-server",
                "keydb-cli",
                "keydb-data",
                "KEYDB_PASSWORD",
                inputs)),
        new(
            "dragonfly",
            "Dragonfly",
            "Redis-compatible in-memory datastore optimized for modern hardware.",
            "1",
            [new ServiceTemplateInput("password", "Password", true, true, null)],
            inputs => $$"""
                        services:
                          dragonfly:
                            image: docker.dragonflydb.io/dragonflydb/dragonfly:latest
                            command: ["--dir=/data", "--requirepass={{Escape(inputs["password"])}}"]
                            environment:
                              DRAGONFLY_PASSWORD: {{Escape(inputs["password"])}}
                            ulimits:
                              memlock: -1
                            volumes:
                              - dragonfly-data:/data
                            ports:
                              - "6379:6379"
                            healthcheck:
                              test: ["CMD-SHELL", "redis-cli -a \"$${DRAGONFLY_PASSWORD}\" ping | grep PONG"]
                              interval: 5s
                              timeout: 5s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          dragonfly-data:
                        """),
        new(
            "elasticsearch",
            "Elasticsearch",
            "Single-node Elasticsearch search engine.",
            "1",
            [
                new ServiceTemplateInput("password", "Elastic password", true, true, null),
                new ServiceTemplateInput("javaOpts", "Java heap options", true, false, "-Xms512m -Xmx512m")
            ],
            inputs => $$"""
                        services:
                          elasticsearch:
                            image: docker.elastic.co/elasticsearch/elasticsearch:8.19.0
                            environment:
                              ELASTIC_PASSWORD: {{Escape(inputs["password"])}}
                              ES_JAVA_OPTS: {{Escape(inputs["javaOpts"])}}
                              discovery.type: single-node
                              bootstrap.memory_lock: "true"
                              xpack.security.http.ssl.enabled: "false"
                            ulimits:
                              memlock:
                                soft: -1
                                hard: -1
                            volumes:
                              - elasticsearch-data:/usr/share/elasticsearch/data
                            ports:
                              - "9200:9200"
                            healthcheck:
                              test: ["CMD-SHELL", "curl --user elastic:$${ELASTIC_PASSWORD} --silent --fail http://localhost:9200/_cluster/health"]
                              interval: 10s
                              timeout: 10s
                              retries: 24
                            restart: unless-stopped
                        volumes:
                          elasticsearch-data:
                        """),
        new(
            "elasticsearch-kibana",
            "Elasticsearch with Kibana",
            "Single-node Elasticsearch with Kibana for search and observability workflows.",
            "1",
            [
                new ServiceTemplateInput("password", "Elastic password", true, true, null),
                new ServiceTemplateInput("javaOpts", "Java heap options", true, false, "-Xms512m -Xmx512m")
            ],
            inputs => $$"""
                        services:
                          elasticsearch:
                            image: docker.elastic.co/elasticsearch/elasticsearch:8.19.0
                            environment:
                              ELASTIC_PASSWORD: {{Escape(inputs["password"])}}
                              ES_JAVA_OPTS: {{Escape(inputs["javaOpts"])}}
                              discovery.type: single-node
                              xpack.security.enabled: "false"
                              xpack.security.http.ssl.enabled: "false"
                            volumes:
                              - elasticsearch-data:/usr/share/elasticsearch/data
                            ports:
                              - "9200:9200"
                            healthcheck:
                              test: ["CMD-SHELL", "curl --silent --fail http://localhost:9200/_cluster/health"]
                              interval: 10s
                              timeout: 10s
                              retries: 24
                            restart: unless-stopped
                          kibana:
                            image: docker.elastic.co/kibana/kibana:8.19.0
                            environment:
                              ELASTICSEARCH_HOSTS: http://elasticsearch:9200
                              TELEMETRY_OPTIN: "false"
                            volumes:
                              - kibana-data:/usr/share/kibana/data
                            ports:
                              - "5601:5601"
                            depends_on:
                              elasticsearch:
                                condition: service_healthy
                            healthcheck:
                              test: ["CMD-SHELL", "curl -s -I http://localhost:5601 | grep -E 'HTTP/1.1 (200|302)'"]
                              interval: 10s
                              timeout: 10s
                              retries: 120
                            restart: unless-stopped
                        volumes:
                          elasticsearch-data:
                          kibana-data:
                        """),
        new(
            "chromadb",
            "ChromaDB",
            "Open-source embedding database.",
            "1",
            [],
            _ => """
                 services:
                   chromadb:
                     image: chromadb/chroma:latest
                     environment:
                       IS_PERSISTENT: "TRUE"
                       CHROMA_PERSIST_PATH: /data
                       ANONYMIZED_TELEMETRY: "FALSE"
                     volumes:
                       - chromadb-data:/data
                     ports:
                       - "8000:8000"
                     healthcheck:
                       test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:8000/api/v2/heartbeat || exit 1"]
                       interval: 10s
                       timeout: 5s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   chromadb-data:
                 """),
        new(
            "mediawiki",
            "MediaWiki",
            "Wiki and documentation platform.",
            "1",
            [],
            _ => """
                 services:
                   mediawiki:
                     image: mediawiki:latest
                     volumes:
                       - mediawiki-images:/var/www/html/images
                       - mediawiki-data:/var/www/html/data
                     ports:
                       - "8080:80"
                     healthcheck:
                       test: ["CMD", "curl", "-f", "http://localhost:80"]
                       interval: 5s
                       timeout: 20s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   mediawiki-images:
                   mediawiki-data:
                 """),
        new(
            "pihole",
            "Pi-hole",
            "Network-wide DNS sinkhole and ad blocker.",
            "1",
            [
                new ServiceTemplateInput("password", "Admin password", true, true, null),
                new ServiceTemplateInput("timezone", "Timezone", true, false, "Etc/UTC"),
                new ServiceTemplateInput("upstreams", "Upstream DNS servers", true, false, "1.1.1.1;8.8.8.8")
            ],
            inputs => $$"""
                        services:
                          pihole:
                            image: pihole/pihole:latest
                            environment:
                              TZ: {{Escape(inputs["timezone"])}}
                              FTLCONF_webserver_api_password: {{Escape(inputs["password"])}}
                              FTLCONF_dns_upstreams: {{Escape(inputs["upstreams"])}}
                              FTLCONF_dns_listeningMode: all
                            volumes:
                              - pihole-etc:/etc/pihole
                              - pihole-dnsmasq:/etc/dnsmasq.d
                            ports:
                              - "53:53/tcp"
                              - "53:53/udp"
                              - "8081:80/tcp"
                            cap_add:
                              - NET_ADMIN
                            healthcheck:
                              test: ["CMD-SHELL", "dig +short +norecurse +retry=0 @127.0.0.1 pi-hole.net || exit 1"]
                              interval: 30s
                              timeout: 10s
                              retries: 5
                            restart: unless-stopped
                        volumes:
                          pihole-etc:
                          pihole-dnsmasq:
                        """),
        new(
            "hoppscotch",
            "Hoppscotch",
            "Open-source API development platform.",
            "1",
            [
                new ServiceTemplateInput("baseUrl", "Base URL", true, false, "http://localhost:3000"),
                new ServiceTemplateInput("postgresUser", "Postgres user", true, false, "hoppscotch"),
                new ServiceTemplateInput("postgresPassword", "Postgres password", true, true, null),
                new ServiceTemplateInput("dataEncryptionKey", "Data encryption key", true, true, null)
            ],
            inputs => $$"""
                        services:
                          hoppscotch:
                            image: hoppscotch/hoppscotch:2026.2.1
                            environment:
                              VITE_ALLOWED_AUTH_PROVIDERS: EMAIL
                              DATABASE_URL: postgresql://{{Escape(inputs["postgresUser"])}}:{{Escape(inputs["postgresPassword"])}}@hoppscotch-db:5432/hoppscotch
                              DATA_ENCRYPTION_KEY: {{Escape(inputs["dataEncryptionKey"])}}
                              WHITELISTED_ORIGINS: {{Escape(inputs["baseUrl"])}}/backend,{{Escape(inputs["baseUrl"])}},{{Escape(inputs["baseUrl"])}}/admin
                              VITE_BASE_URL: {{Escape(inputs["baseUrl"])}}
                              VITE_SHORTCODE_BASE_URL: {{Escape(inputs["baseUrl"])}}
                              VITE_ADMIN_URL: {{Escape(inputs["baseUrl"])}}/admin
                              VITE_BACKEND_GQL_URL: {{Escape(inputs["baseUrl"])}}/backend/graphql
                              VITE_BACKEND_WS_URL: ws://localhost:3000/backend/graphql
                              VITE_BACKEND_API_URL: {{Escape(inputs["baseUrl"])}}/backend/v1
                              ENABLE_SUBPATH_BASED_ACCESS: "true"
                            depends_on:
                              hoppscotch-migration:
                                condition: service_completed_successfully
                            ports:
                              - "3000:80"
                            healthcheck:
                              test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:80/"]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                          hoppscotch-db:
                            image: postgres:15
                            environment:
                              POSTGRES_USER: {{Escape(inputs["postgresUser"])}}
                              POSTGRES_PASSWORD: {{Escape(inputs["postgresPassword"])}}
                              POSTGRES_DB: hoppscotch
                            volumes:
                              - hoppscotch-postgres-data:/var/lib/postgresql/data
                            healthcheck:
                              test: ["CMD-SHELL", "pg_isready -h localhost -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                              interval: 5s
                              timeout: 10s
                              retries: 10
                            restart: unless-stopped
                          hoppscotch-migration:
                            image: hoppscotch/hoppscotch:2026.2.1
                            command: pnpx prisma migrate deploy
                            environment:
                              DATABASE_URL: postgres://{{Escape(inputs["postgresUser"])}}:{{Escape(inputs["postgresPassword"])}}@hoppscotch-db:5432/hoppscotch
                            depends_on:
                              hoppscotch-db:
                                condition: service_healthy
                            restart: on-failure
                        volumes:
                          hoppscotch-postgres-data:
                        """),
        new(
            "proxyscotch",
            "ProxyScotch",
            "CORS proxy for Hoppscotch.",
            "1",
            [
                new ServiceTemplateInput("token", "Proxy token", true, true, null),
                new ServiceTemplateInput("allowedOrigins", "Allowed origins", true, false, "*")
            ],
            inputs => $$"""
                        services:
                          proxyscotch:
                            image: hoppscotch/proxyscotch:v0.1.4
                            environment:
                              PROXYSCOTCH_TOKEN: {{Escape(inputs["token"])}}
                              PROXYSCOTCH_ALLOWED_ORIGINS: {{Escape(inputs["allowedOrigins"])}}
                            ports:
                              - "9159:9159"
                            restart: unless-stopped
                        """),
        new(
            "strapi",
            "Strapi",
            "Open-source headless CMS with PostgreSQL.",
            "1",
            [
                new ServiceTemplateInput("database", "Database", true, false, "strapi"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "strapi"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null),
                new ServiceTemplateInput("appKeys", "App keys", true, true, null),
                new ServiceTemplateInput("jwtSecret", "JWT secret", true, true, null),
                new ServiceTemplateInput("adminJwtSecret", "Admin JWT secret", true, true, null)
            ],
            inputs => $$"""
                        services:
                          strapi:
                            image: elestio/strapi-production:v5.33.4
                            environment:
                              DATABASE_CLIENT: postgres
                              DATABASE_HOST: strapi-postgres
                              DATABASE_PORT: "5432"
                              DATABASE_NAME: {{Escape(inputs["database"])}}
                              DATABASE_USERNAME: {{Escape(inputs["databaseUser"])}}
                              DATABASE_PASSWORD: {{Escape(inputs["databasePassword"])}}
                              APP_KEYS: {{Escape(inputs["appKeys"])}}
                              JWT_SECRET: {{Escape(inputs["jwtSecret"])}}
                              ADMIN_JWT_SECRET: {{Escape(inputs["adminJwtSecret"])}}
                              STRAPI_TELEMETRY_DISABLED: "true"
                              NODE_ENV: production
                            volumes:
                              - strapi-config:/opt/app/config
                              - strapi-src:/opt/app/src
                              - strapi-uploads:/opt/app/public/uploads
                            ports:
                              - "1337:1337"
                            depends_on:
                              strapi-postgres:
                                condition: service_healthy
                            healthcheck:
                              test: ["CMD", "wget", "-q", "--spider", "http://127.0.0.1:1337/"]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                          strapi-postgres:
                            image: postgres:17
                            environment:
                              POSTGRES_DB: {{Escape(inputs["database"])}}
                              POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                              POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - strapi-postgres-data:/var/lib/postgresql/data
                            healthcheck:
                              test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                        volumes:
                          strapi-config:
                          strapi-src:
                          strapi-uploads:
                          strapi-postgres-data:
                        """),
        new(
            "supabase",
            "Supabase",
            "Compact self-hosted Supabase project stack.",
            "1",
            [
                new ServiceTemplateInput("postgresPassword", "Postgres password", true, true, null),
                new ServiceTemplateInput("jwtSecret", "JWT secret", true, true, null),
                new ServiceTemplateInput("anonKey", "Anon API key", true, true, null),
                new ServiceTemplateInput("serviceRoleKey", "Service role API key", true, true, null),
                new ServiceTemplateInput("dashboardUsername", "Dashboard username", true, false, "supabase"),
                new ServiceTemplateInput("dashboardPassword", "Dashboard password", true, true, null),
                new ServiceTemplateInput("siteUrl", "Site URL", true, false, "http://localhost:8000")
            ],
            inputs => $$"""
                        services:
                          supabase-db:
                            image: supabase/postgres:17.4.1.074
                            environment:
                              POSTGRES_PASSWORD: {{Escape(inputs["postgresPassword"])}}
                              POSTGRES_DB: postgres
                              JWT_SECRET: {{Escape(inputs["jwtSecret"])}}
                              JWT_EXP: "3600"
                            volumes:
                              - supabase-db-data:/var/lib/postgresql/data
                            ports:
                              - "5432:5432"
                            healthcheck:
                              test: ["CMD-SHELL", "pg_isready -U postgres -d postgres"]
                              interval: 5s
                              timeout: 10s
                              retries: 10
                            restart: unless-stopped
                          supabase-studio:
                            image: supabase/studio:latest
                            environment:
                              STUDIO_PG_META_URL: http://supabase-meta:8080
                              POSTGRES_PASSWORD: {{Escape(inputs["postgresPassword"])}}
                              DEFAULT_ORGANIZATION_NAME: Vessel
                              DEFAULT_PROJECT_NAME: Vessel
                              SUPABASE_URL: {{Escape(inputs["siteUrl"])}}
                              SUPABASE_PUBLIC_URL: {{Escape(inputs["siteUrl"])}}
                              SUPABASE_ANON_KEY: {{Escape(inputs["anonKey"])}}
                              SUPABASE_SERVICE_KEY: {{Escape(inputs["serviceRoleKey"])}}
                            ports:
                              - "3001:3000"
                            depends_on:
                              supabase-meta:
                                condition: service_started
                            restart: unless-stopped
                          supabase-meta:
                            image: supabase/postgres-meta:latest
                            environment:
                              PG_META_PORT: "8080"
                              PG_META_DB_HOST: supabase-db
                              PG_META_DB_PORT: "5432"
                              PG_META_DB_NAME: postgres
                              PG_META_DB_USER: postgres
                              PG_META_DB_PASSWORD: {{Escape(inputs["postgresPassword"])}}
                            depends_on:
                              supabase-db:
                                condition: service_healthy
                            restart: unless-stopped
                          supabase-rest:
                            image: postgrest/postgrest:latest
                            environment:
                              PGRST_DB_URI: postgres://postgres:{{Escape(inputs["postgresPassword"])}}@supabase-db:5432/postgres
                              PGRST_DB_SCHEMAS: public,storage,graphql_public
                              PGRST_DB_ANON_ROLE: anon
                              PGRST_JWT_SECRET: {{Escape(inputs["jwtSecret"])}}
                            ports:
                              - "3002:3000"
                            depends_on:
                              supabase-db:
                                condition: service_healthy
                            restart: unless-stopped
                          supabase-auth:
                            image: supabase/gotrue:latest
                            environment:
                              GOTRUE_API_HOST: 0.0.0.0
                              GOTRUE_API_PORT: "9999"
                              API_EXTERNAL_URL: {{Escape(inputs["siteUrl"])}}/auth/v1
                              GOTRUE_SITE_URL: {{Escape(inputs["siteUrl"])}}
                              GOTRUE_DB_DRIVER: postgres
                              GOTRUE_DB_DATABASE_URL: postgres://postgres:{{Escape(inputs["postgresPassword"])}}@supabase-db:5432/postgres?sslmode=disable
                              GOTRUE_JWT_SECRET: {{Escape(inputs["jwtSecret"])}}
                              GOTRUE_JWT_ADMIN_ROLES: service_role
                              GOTRUE_JWT_AUD: authenticated
                              GOTRUE_EXTERNAL_EMAIL_ENABLED: "true"
                              GOTRUE_MAILER_AUTOCONFIRM: "true"
                              DASHBOARD_USERNAME: {{Escape(inputs["dashboardUsername"])}}
                              DASHBOARD_PASSWORD: {{Escape(inputs["dashboardPassword"])}}
                            ports:
                              - "9999:9999"
                            depends_on:
                              supabase-db:
                                condition: service_healthy
                            restart: unless-stopped
                        volumes:
                          supabase-db-data:
                        """),
        new(
            "wireguard-easy",
            "WireGuard Easy",
            "WireGuard VPN with a web administration UI.",
            "1",
            [
                new ServiceTemplateInput("host", "Public host", true, false, "vpn.example.com"),
                new ServiceTemplateInput("password", "Admin password", true, true, null)
            ],
            inputs => $$"""
                        services:
                          wg-easy:
                            image: ghcr.io/wg-easy/wg-easy:latest
                            environment:
                              WG_HOST: {{Escape(inputs["host"])}}
                              WG_PORT: "51820"
                              PORT: "51821"
                              PASSWORD: {{Escape(inputs["password"])}}
                            volumes:
                              - wg-easy-data:/etc/wireguard
                            ports:
                              - "51820:51820/udp"
                              - "51821:51821/tcp"
                            cap_add:
                              - NET_ADMIN
                              - SYS_MODULE
                            sysctls:
                              net.ipv4.conf.all.src_valid_mark: "1"
                              net.ipv4.ip_forward: "1"
                            restart: unless-stopped
                        volumes:
                          wg-easy-data:
                        """),
        new(
            "wordpress-without-database",
            "WordPress (without a database)",
            "WordPress application container configured for an external database.",
            "1",
            [],
            _ => WordpressCompose(null)),
        new(
            "wordpress-mariadb",
            "WordPress (with MariaDB)",
            "WordPress with a bundled MariaDB database.",
            "1",
            [
                new ServiceTemplateInput("database", "Database", true, false, "wordpress"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "wordpress"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null),
                new ServiceTemplateInput("rootPassword", "Database root password", true, true, null)
            ],
            inputs => WordpressCompose(("mariadb", "mariadb:11", "mariadb-data", inputs))),
        new(
            "wordpress-mysql",
            "WordPress (with MySQL)",
            "WordPress with a bundled MySQL database.",
            "1",
            [
                new ServiceTemplateInput("database", "Database", true, false, "wordpress"),
                new ServiceTemplateInput("databaseUser", "Database user", true, false, "wordpress"),
                new ServiceTemplateInput("databasePassword", "Database password", true, true, null),
                new ServiceTemplateInput("rootPassword", "Database root password", true, true, null)
            ],
            inputs => WordpressCompose(("mysql", "mysql:8", "mysql-data", inputs))),
        new(
            "tailscale-client",
            "Tailscale Client",
            "Tailscale node container for joining a tailnet.",
            "1",
            [
                new ServiceTemplateInput("authKey", "Auth key", true, true, null),
                new ServiceTemplateInput("hostname", "Hostname", true, false, "vessel-ts")
            ],
            inputs => $$"""
                        services:
                          tailscale-client:
                            image: tailscale/tailscale:latest
                            hostname: {{Escape(inputs["hostname"])}}
                            environment:
                              TS_HOSTNAME: {{Escape(inputs["hostname"])}}
                              TS_AUTHKEY: {{Escape(inputs["authKey"])}}
                              TS_STATE_DIR: /var/lib/tailscale
                              TS_USERSPACE: "false"
                            volumes:
                              - tailscale-client-data:/var/lib/tailscale
                            devices:
                              - /dev/net/tun:/dev/net/tun
                            cap_add:
                              - NET_ADMIN
                            healthcheck:
                              test: ["CMD-SHELL", "tailscale status --json | grep -q 'BackendState'"]
                              interval: 10s
                              timeout: 5s
                              retries: 5
                            restart: unless-stopped
                        volumes:
                          tailscale-client-data:
                        """)
    ];

    public IReadOnlyList<ServiceTemplateSummary> List()
    {
        return Templates
            .Select(template => new ServiceTemplateSummary(template.Key, template.Name, template.Description,
                template.Version, template.Inputs))
            .ToArray();
    }

    public ServiceTemplateDefinition Get(string key)
    {
        return Templates.SingleOrDefault(template => string.Equals(template.Key, key, StringComparison.Ordinal))
               ?? throw new DomainException("Service template was not found.");
    }

    public ServiceProvisioningPlan CreatePlan(string templateKey, Guid serviceId, string serviceName,
        IReadOnlyDictionary<string, string> inputs)
    {
        ServiceTemplateDefinition template = Get(templateKey);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ServiceTemplateInput input in template.Inputs)
        {
            inputs.TryGetValue(input.Key, out var value);
            value = string.IsNullOrWhiteSpace(value) ? input.DefaultValue : value;
            if (input.Required && string.IsNullOrWhiteSpace(value))
                throw new DomainException($"Template input '{input.Key}' is required.");
            if (!string.IsNullOrWhiteSpace(value))
                resolved[input.Key] = value;
        }

        var compose = template.ComposeFactory(resolved);
        foreach (ServiceTemplateInput input in template.Inputs.Where(input =>
                     input.Secret && resolved.ContainsKey(input.Key)))
            compose = compose.Replace(resolved[input.Key], $"${{{EnvironmentKey(input.Key)}}}",
                StringComparison.Ordinal);
        return new ServiceProvisioningPlan(
            $"vessel-svc-{serviceId:N}"[..39],
            Slug(serviceName),
            compose,
            template.Inputs.Where(input => input.Secret && resolved.ContainsKey(input.Key))
                .ToDictionary(input => input.Key, input => resolved[input.Key], StringComparer.Ordinal));
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string Optional(IReadOnlyDictionary<string, string> inputs, string key)
    {
        return inputs.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string Slug(string value)
    {
        var normalized = ServiceNameRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "service" : normalized[..Math.Min(40, normalized.Length)];
    }

    private static string PostgresCompose(
        string serviceName,
        string image,
        string volumeName,
        IReadOnlyDictionary<string, string> inputs)
    {
        return $$"""
                 services:
                   {{serviceName}}:
                     image: {{image}}
                     environment:
                       POSTGRES_DB: {{Escape(inputs["database"])}}
                       POSTGRES_USER: {{Escape(inputs["username"])}}
                       POSTGRES_PASSWORD: {{Escape(inputs["password"])}}
                     volumes:
                       - {{volumeName}}:/var/lib/postgresql/data
                     ports:
                       - "5432:5432"
                     healthcheck:
                       test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                       interval: 5s
                       timeout: 5s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   {{volumeName}}:
                 """;
    }

    private static string RedisCompatibleStoreCompose(
        string serviceName,
        string image,
        string serverCommand,
        string cliCommand,
        string volumeName,
        string passwordEnvironmentKey,
        IReadOnlyDictionary<string, string> inputs)
    {
        return $$"""
                 services:
                   {{serviceName}}:
                     image: {{image}}
                     command: ["{{serverCommand}}", "--appendonly", "yes", "--requirepass", "{{Escape(inputs["password"])}}"]
                     environment:
                       {{passwordEnvironmentKey}}: {{Escape(inputs["password"])}}
                     volumes:
                       - {{volumeName}}:/data
                     ports:
                       - "6379:6379"
                     healthcheck:
                       test: ["CMD-SHELL", "{{cliCommand}} -a \"$${{{passwordEnvironmentKey}}}\" ping | grep PONG"]
                       interval: 5s
                       timeout: 5s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   {{volumeName}}:
                 """;
    }

    private static string WordpressCompose(
        (string ServiceName, string Image, string VolumeName, IReadOnlyDictionary<string, string> Inputs)? database)
    {
        if (database is null)
            return """
                   services:
                     wordpress:
                       image: wordpress:latest
                       volumes:
                         - wordpress-files:/var/www/html
                       ports:
                         - "8082:80"
                       healthcheck:
                         test: ["CMD", "curl", "-f", "http://127.0.0.1"]
                         interval: 2s
                         timeout: 10s
                         retries: 10
                       restart: unless-stopped
                   volumes:
                     wordpress-files:
                   """;

        (var serviceName, var image, var volumeName, IReadOnlyDictionary<string, string> inputs) = database.Value;
        return $$"""
                 services:
                   wordpress:
                     image: wordpress:latest
                     volumes:
                       - wordpress-files:/var/www/html
                     environment:
                       WORDPRESS_DB_HOST: {{serviceName}}:3306
                       WORDPRESS_DB_USER: {{Escape(inputs["databaseUser"])}}
                       WORDPRESS_DB_PASSWORD: {{Escape(inputs["databasePassword"])}}
                       WORDPRESS_DB_NAME: {{Escape(inputs["database"])}}
                     ports:
                       - "8082:80"
                     depends_on:
                       {{serviceName}}:
                         condition: service_healthy
                     healthcheck:
                       test: ["CMD", "curl", "-f", "http://127.0.0.1"]
                       interval: 2s
                       timeout: 10s
                       retries: 10
                     restart: unless-stopped
                   {{serviceName}}:
                     image: {{image}}
                     volumes:
                       - {{volumeName}}:/var/lib/mysql
                     environment:
                       MYSQL_ROOT_PASSWORD: {{Escape(inputs["rootPassword"])}}
                       MYSQL_DATABASE: {{Escape(inputs["database"])}}
                       MYSQL_USER: {{Escape(inputs["databaseUser"])}}
                       MYSQL_PASSWORD: {{Escape(inputs["databasePassword"])}}
                     healthcheck:
                       test: ["CMD-SHELL", "mysqladmin ping -h 127.0.0.1 -u root -p\"$${MYSQL_ROOT_PASSWORD}\""]
                       interval: 5s
                       timeout: 20s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   wordpress-files:
                   {{volumeName}}:
                 """;
    }

    private static IReadOnlyList<ServiceTemplateInput> GiteaDatabaseInputs()
    {
        return
        [
            new ServiceTemplateInput("database", "Database", true, false, "gitea"),
            new ServiceTemplateInput("databaseUser", "Database user", true, false, "gitea"),
            new ServiceTemplateInput("databasePassword", "Database password", true, true, null),
            new ServiceTemplateInput("rootPassword", "Database root password", true, true, null),
            new ServiceTemplateInput("userUid", "User UID", true, false, "1000"),
            new ServiceTemplateInput("userGid", "User GID", true, false, "1000"),
            new ServiceTemplateInput("sshPort", "SSH host port", true, false, "22222")
        ];
    }

    private static string GiteaWithDatabaseCompose(
        string databaseServiceName,
        string databaseImage,
        string giteaDatabaseType,
        string databaseVolumeName,
        IReadOnlyDictionary<string, string> inputs)
    {
        var databaseService = databaseServiceName == "postgresql"
            ? $$"""
                      {{databaseServiceName}}:
                        image: {{databaseImage}}
                        environment:
                          POSTGRES_DB: {{Escape(inputs["database"])}}
                          POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                          POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                        volumes:
                          - {{databaseVolumeName}}:/var/lib/postgresql/data
                        healthcheck:
                          test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                          interval: 5s
                          timeout: 20s
                          retries: 10
                        restart: unless-stopped
                """
            : $$"""
                      {{databaseServiceName}}:
                        image: {{databaseImage}}
                        environment:
                          MYSQL_ROOT_PASSWORD: {{Escape(inputs["rootPassword"])}}
                          MYSQL_DATABASE: {{Escape(inputs["database"])}}
                          MYSQL_USER: {{Escape(inputs["databaseUser"])}}
                          MYSQL_PASSWORD: {{Escape(inputs["databasePassword"])}}
                        volumes:
                          - {{databaseVolumeName}}:/var/lib/mysql
                        healthcheck:
                          test: ["CMD-SHELL", "mysqladmin ping -h 127.0.0.1 -u root -p\"$${MYSQL_ROOT_PASSWORD}\""]
                          interval: 5s
                          timeout: 20s
                          retries: 10
                        restart: unless-stopped
                """;

        return $$"""
                 services:
                   gitea:
                     image: gitea/gitea:latest
                     environment:
                       USER_UID: {{Escape(inputs["userUid"])}}
                       USER_GID: {{Escape(inputs["userGid"])}}
                       GITEA__database__DB_TYPE: {{giteaDatabaseType}}
                       GITEA__database__HOST: {{databaseServiceName}}:{{(databaseServiceName == "postgresql" ? "5432" : "3306")}}
                       GITEA__database__NAME: {{Escape(inputs["database"])}}
                       GITEA__database__USER: {{Escape(inputs["databaseUser"])}}
                       GITEA__database__PASSWD: {{Escape(inputs["databasePassword"])}}
                     volumes:
                       - gitea-data:/data
                     ports:
                       - "3003:3000"
                       - "{{Escape(inputs["sshPort"])}}:22"
                     depends_on:
                       {{databaseServiceName}}:
                         condition: service_healthy
                     healthcheck:
                       test: ["CMD", "curl", "-f", "http://127.0.0.1:3000"]
                       interval: 2s
                       timeout: 10s
                       retries: 15
                     restart: unless-stopped
                 {{databaseService}}
                 volumes:
                   gitea-data:
                   {{databaseVolumeName}}:
                 """;
    }

    private static string MatrixSynapseCompose(string storage, IReadOnlyDictionary<string, string> inputs)
    {
        if (storage == "sqlite")
            return $$"""
                     services:
                       synapse:
                         image: matrixdotorg/synapse:latest
                         environment:
                           SYNAPSE_SERVER_NAME: {{Escape(inputs["serverName"])}}
                           SYNAPSE_REPORT_STATS: {{Escape(inputs["reportStats"])}}
                         volumes:
                           - synapse-data:/data
                         ports:
                           - "8008:8008"
                         command:
                           - sh
                           - -c
                           - |
                             if [ ! -f /data/homeserver.yaml ]; then
                               python -m synapse.app.homeserver --server-name "$${SYNAPSE_SERVER_NAME}" --config-path /data/homeserver.yaml --generate-config --report-stats "$${SYNAPSE_REPORT_STATS}";
                             fi
                             exec python -m synapse.app.homeserver --config-path /data/homeserver.yaml
                         healthcheck:
                           test: ["CMD-SHELL", "python -c \"import urllib.request; urllib.request.urlopen('http://127.0.0.1:8008/health', timeout=5)\""]
                           interval: 10s
                           timeout: 10s
                           retries: 20
                         restart: unless-stopped
                     volumes:
                       synapse-data:
                     """;

        return $$"""
                 services:
                   synapse:
                     image: matrixdotorg/synapse:latest
                     environment:
                       SYNAPSE_SERVER_NAME: {{Escape(inputs["serverName"])}}
                       SYNAPSE_REPORT_STATS: {{Escape(inputs["reportStats"])}}
                       SYNAPSE_POSTGRES_DB: {{Escape(inputs["database"])}}
                       SYNAPSE_POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                       SYNAPSE_POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                     volumes:
                       - synapse-data:/data
                     ports:
                       - "8008:8008"
                     depends_on:
                       synapse-postgres:
                         condition: service_healthy
                     command:
                       - sh
                       - -c
                       - |
                         if [ ! -f /data/homeserver.yaml ]; then
                           python -m synapse.app.homeserver --server-name "$${SYNAPSE_SERVER_NAME}" --config-path /data/homeserver.yaml --generate-config --report-stats "$${SYNAPSE_REPORT_STATS}";
                           printf '%s\n' 'database:' '  name: psycopg2' '  args:' "    user: $${SYNAPSE_POSTGRES_USER}" "    password: $${SYNAPSE_POSTGRES_PASSWORD}" "    database: $${SYNAPSE_POSTGRES_DB}" '    host: synapse-postgres' '    port: 5432' '    cp_min: 5' '    cp_max: 10' >> /data/homeserver.yaml;
                         fi
                         exec python -m synapse.app.homeserver --config-path /data/homeserver.yaml
                     healthcheck:
                       test: ["CMD-SHELL", "python -c \"import urllib.request; urllib.request.urlopen('http://127.0.0.1:8008/health', timeout=5)\""]
                       interval: 10s
                       timeout: 10s
                       retries: 20
                     restart: unless-stopped
                   synapse-postgres:
                     image: postgres:16-alpine
                     environment:
                       POSTGRES_DB: {{Escape(inputs["database"])}}
                       POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                       POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                     volumes:
                       - synapse-postgresql-data:/var/lib/postgresql/data
                     healthcheck:
                       test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                       interval: 5s
                       timeout: 20s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   synapse-data:
                   synapse-postgresql-data:
                 """;
    }

    private static string KeycloakCompose(string? database, IReadOnlyDictionary<string, string> inputs)
    {
        if (database is null)
            return $$"""
                     services:
                       keycloak:
                         image: quay.io/keycloak/keycloak:latest
                         command: ["start-dev"]
                         environment:
                           KC_BOOTSTRAP_ADMIN_USERNAME: {{Escape(inputs["adminUsername"])}}
                           KC_BOOTSTRAP_ADMIN_PASSWORD: {{Escape(inputs["adminPassword"])}}
                           KC_HOSTNAME: {{Escape(Optional(inputs, "hostname"))}}
                           KC_HEALTH_ENABLED: "true"
                           KC_HTTP_ENABLED: "true"
                           TZ: {{Escape(inputs["timezone"])}}
                         ports:
                           - "8087:8080"
                         healthcheck:
                           test: ["CMD-SHELL", "bash -c ':> /dev/tcp/127.0.0.1/8080' || exit 1"]
                           interval: 10s
                           timeout: 10s
                           retries: 20
                         restart: unless-stopped
                     """;

        return $$"""
                 services:
                   keycloak:
                     image: quay.io/keycloak/keycloak:latest
                     command: ["start-dev"]
                     environment:
                       KC_BOOTSTRAP_ADMIN_USERNAME: {{Escape(inputs["adminUsername"])}}
                       KC_BOOTSTRAP_ADMIN_PASSWORD: {{Escape(inputs["adminPassword"])}}
                       KC_HOSTNAME: {{Escape(Optional(inputs, "hostname"))}}
                       KC_HEALTH_ENABLED: "true"
                       KC_HTTP_ENABLED: "true"
                       KC_DB: postgres
                       KC_DB_URL_HOST: keycloak-postgresql
                       KC_DB_URL_DATABASE: {{Escape(inputs["database"])}}
                       KC_DB_USERNAME: {{Escape(inputs["databaseUser"])}}
                       KC_DB_PASSWORD: {{Escape(inputs["databasePassword"])}}
                       TZ: {{Escape(inputs["timezone"])}}
                     ports:
                       - "8087:8080"
                     depends_on:
                       keycloak-postgresql:
                         condition: service_healthy
                     healthcheck:
                       test: ["CMD-SHELL", "bash -c ':> /dev/tcp/127.0.0.1/8080' || exit 1"]
                       interval: 10s
                       timeout: 10s
                       retries: 20
                     restart: unless-stopped
                   keycloak-postgresql:
                     image: postgres:16-alpine
                     environment:
                       POSTGRES_DB: {{Escape(inputs["database"])}}
                       POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                       POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                     volumes:
                       - keycloak-postgresql-data:/var/lib/postgresql/data
                     healthcheck:
                       test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                       interval: 5s
                       timeout: 20s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   keycloak-postgresql-data:
                 """;
    }

    private static string DrupalWithPostgresCompose(IReadOnlyDictionary<string, string> inputs)
    {
        return $$"""
                 services:
                   drupal:
                     image: drupal:latest
                     volumes:
                       - drupal-modules:/var/www/html/modules
                       - drupal-profiles:/var/www/html/profiles
                       - drupal-sites:/var/www/html/sites
                       - drupal-themes:/var/www/html/themes
                     ports:
                       - "8089:80"
                     depends_on:
                       drupal-postgresql:
                         condition: service_healthy
                     healthcheck:
                       test: ["CMD", "curl", "-f", "http://127.0.0.1"]
                       interval: 10s
                       timeout: 10s
                       retries: 20
                     restart: unless-stopped
                   drupal-postgresql:
                     image: postgres:16-alpine
                     environment:
                       POSTGRES_DB: {{Escape(inputs["database"])}}
                       POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                       POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                     volumes:
                       - drupal-postgresql-data:/var/lib/postgresql/data
                     healthcheck:
                       test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                       interval: 5s
                       timeout: 20s
                       retries: 10
                     restart: unless-stopped
                 volumes:
                   drupal-modules:
                   drupal-profiles:
                   drupal-sites:
                   drupal-themes:
                   drupal-postgresql-data:
                 """;
    }

    private static IReadOnlyList<ServiceTemplateInput> NextcloudInputs(bool database, bool rootPassword = false)
    {
        var inputs = new List<ServiceTemplateInput>
        {
            new("adminUsername", "Admin username", true, false, "admin"),
            new("adminPassword", "Admin password", true, true, null),
            new("trustedDomains", "Trusted domains", false, false, null)
        };

        if (database)
        {
            inputs.Add(new ServiceTemplateInput("database", "Database", true, false, "nextcloud"));
            inputs.Add(new ServiceTemplateInput("databaseUser", "Database user", true, false, "nextcloud"));
            inputs.Add(new ServiceTemplateInput("databasePassword", "Database password", true, true, null));
        }

        if (rootPassword)
            inputs.Add(new ServiceTemplateInput("rootPassword", "Database root password", true, true, null));

        return inputs;
    }

    private static string NextcloudCompose(string? database, IReadOnlyDictionary<string, string> inputs)
    {
        var appEnvironment = database switch
        {
            "postgres" => $$"""
                              POSTGRES_HOST: nextcloud-postgresql
                              POSTGRES_DB: {{Escape(inputs["database"])}}
                              POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                              POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                """,
            "mysql" or "mariadb" => $$"""
                              MYSQL_HOST: nextcloud-{{database}}
                              MYSQL_DATABASE: {{Escape(inputs["database"])}}
                              MYSQL_USER: {{Escape(inputs["databaseUser"])}}
                              MYSQL_PASSWORD: {{Escape(inputs["databasePassword"])}}
                """,
            _ => string.Empty
        };

        var dependsOn = database is null
            ? string.Empty
            : $$"""
                            depends_on:
                              nextcloud-{{(database == "postgres" ? "postgresql" : database)}}:
                                condition: service_healthy
              """;

        var databaseService = database switch
        {
            "postgres" => $$"""
                          nextcloud-postgresql:
                            image: postgres:16-alpine
                            environment:
                              POSTGRES_DB: {{Escape(inputs["database"])}}
                              POSTGRES_USER: {{Escape(inputs["databaseUser"])}}
                              POSTGRES_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - nextcloud-postgresql-data:/var/lib/postgresql/data
                            healthcheck:
                              test: ["CMD-SHELL", "pg_isready -U \"$${POSTGRES_USER}\" -d \"$${POSTGRES_DB}\""]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                """,
            "mysql" => $$"""
                          nextcloud-mysql:
                            image: mysql:8
                            environment:
                              MYSQL_ROOT_PASSWORD: {{Escape(inputs["rootPassword"])}}
                              MYSQL_DATABASE: {{Escape(inputs["database"])}}
                              MYSQL_USER: {{Escape(inputs["databaseUser"])}}
                              MYSQL_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - nextcloud-mysql-data:/var/lib/mysql
                            healthcheck:
                              test: ["CMD-SHELL", "mysqladmin ping -h 127.0.0.1 -u root -p\"$${MYSQL_ROOT_PASSWORD}\""]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                """,
            "mariadb" => $$"""
                          nextcloud-mariadb:
                            image: mariadb:11
                            environment:
                              MYSQL_ROOT_PASSWORD: {{Escape(inputs["rootPassword"])}}
                              MYSQL_DATABASE: {{Escape(inputs["database"])}}
                              MYSQL_USER: {{Escape(inputs["databaseUser"])}}
                              MYSQL_PASSWORD: {{Escape(inputs["databasePassword"])}}
                            volumes:
                              - nextcloud-mariadb-data:/var/lib/mysql
                            healthcheck:
                              test: ["CMD-SHELL", "mariadb-admin ping -h 127.0.0.1 -u root -p\"$${MYSQL_ROOT_PASSWORD}\""]
                              interval: 5s
                              timeout: 20s
                              retries: 10
                            restart: unless-stopped
                """,
            _ => string.Empty
        };

        var databaseVolume = database switch
        {
            "postgres" => "  nextcloud-postgresql-data:\n",
            "mysql" => "  nextcloud-mysql-data:\n",
            "mariadb" => "  nextcloud-mariadb-data:\n",
            _ => string.Empty
        };

        return $$"""
                 services:
                   nextcloud:
                     image: nextcloud:latest
                     environment:
                       NEXTCLOUD_ADMIN_USER: {{Escape(inputs["adminUsername"])}}
                       NEXTCLOUD_ADMIN_PASSWORD: {{Escape(inputs["adminPassword"])}}
                       NEXTCLOUD_TRUSTED_DOMAINS: {{Escape(Optional(inputs, "trustedDomains"))}}
                 {{appEnvironment}}
                     volumes:
                       - nextcloud-data:/var/www/html
                     ports:
                       - "8088:80"
                 {{dependsOn}}
                     healthcheck:
                       test: ["CMD", "curl", "-f", "http://127.0.0.1/status.php"]
                       interval: 10s
                       timeout: 10s
                       retries: 20
                     restart: unless-stopped
                 {{databaseService}}
                 volumes:
                   nextcloud-data:
                 {{databaseVolume}}
                 """;
    }

    public static string EnvironmentKey(string key)
    {
        return new string(key.Select(character => char.IsLetterOrDigit(character)
                ? char.ToUpperInvariant(character)
                : '_')
            .ToArray());
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex ServiceNameRegex();
}

public sealed record ServiceTemplateDefinition(
    string Key,
    string Name,
    string Description,
    string Version,
    IReadOnlyList<ServiceTemplateInput> Inputs,
    Func<IReadOnlyDictionary<string, string>, string> ComposeFactory);
