# Build packs, environment values and Docker Compose policy

Covers gap report items DEP-06 (registry rollback), DEP-09 (build packs), DEP-10 (environment value semantics),
DEP-11/SEC-11 (Compose validation and host-path policy) and the Docker half of SEC-10. Upstream reference: Coolify
`ApplicationDeploymentJob` (Railpack, static, registry push, build-time variables), `HasSecretManager` /
`escapeComposeEnvFileValue` (exact values), `bootstrap/helpers/parsers.php` (`validateComposeServiceScale`, Compose
injection checks) and the `uses_legacy_escaping` / `preserve_compose_null` migrations.

## Build packs

| Pack | How it builds |
| --- | --- |
| Railpack (recommended, default in the UI) | `railpack prepare` in the pinned CLI image (`ComponentVersions.RailpackCliImage`), then `docker buildx build` on a `docker-container` builder (`vessel-railpack`, pinned `BuildKitImage`) with the pinned Railpack frontend as `BUILDKIT_SYNTAX`. Install command → `RAILPACK_INSTALL_CMD`; build/start → `--build-cmd`/`--start-cmd`; `curl` and `wget` are added to `RAILPACK_DEPLOY_APT_PACKAGES`. One Railpack build per server at a time (lock `railpack-builder:{serverId}`); the builder is stopped after each build. Docker only (Podman servers get a clear error). |
| Static | Generated nginx image (`ComponentVersions.StaticNginxImage`) serving the publish directory (relative to the base directory) on the application port. `IsSpa` adds the `/index.html` fallback. Dot files (except `.well-known`) are never served and `.git`/`.env` are excluded from the build context. |
| Dockerfile | The repository Dockerfile, relative to the base directory. |
| Nixpacks (deprecated, like upstream v4.5.0) | `nixpacks build --out` (needs the `nixpacks` CLI on the server), then the generated Dockerfile. Every deployment logs the deprecation. |
| Docker Compose | Services build during `docker compose up --build`. |

Images Vessel builds are deployed by an image-only stack and `compose up --no-build`, so Compose never rebuilds them
without build values. The build context must be a directory inside the checkout without symbolic links.

Build settings: `GET/PATCH /api/v1/applications/{id}/build-settings` (write permission; audited as
`application.build_settings_updated`).

## Build-time variables

Only variables marked "available at build time" reach a build; runtime-only values never do, and build-only values
never reach the container. They are passed as `--build-arg NAME` (Dockerfile, Static, Nixpacks), BuildKit secrets
`--secret id=NAME,env=NAME` (Railpack) or `build.args: NAME: ${NAME}` (Compose). Values never appear on a command
line: locally they are set in the runtime CLI's environment, which starts from an allowlist (the daemon's own
secrets are not inherited); on SSH servers a fixed wrapper sources `build/build.env` (single-quoted `KEY='value'`
lines) from the uploaded workspace. Skipped with a warning (name only): dotted names, names reserved for the
runtime client or process loader (`DOCKER_HOST`, `DOCKER_CONFIG`, `BUILDX_*`, `HOME`, `PATH`, `LD_*`, ...), and
preserved Compose nulls.

## Registry push and rollback (DEP-06)

With `RegistryImage` (for example `ghcr.io/acme/web`) production images are named `{RegistryImage}:{commit}` and
pushed after the build (not for previews or Compose). With a team `RegistryCredentialId` the CLI logs in with an
isolated configuration (`docker --config <workspace>/registry`, `podman --authfile`), the password on standard
input, and logs out afterwards. A rollback whose image is no longer local is pulled from the registry; if that
fails the target commit is rebuilt.

## Environment values (DEP-10)

- Values are stored exactly: no trimming, empty values allowed, multiline values keep their trailing newline; NUL is
  rejected. Keys follow upstream `[A-Za-z_][A-Za-z0-9_.]*`.
- Interpolation (`$NAME`, `${NAME}`, `${NAME:-default}`, `${NAME:?message}`, `$$`) is done by Vessel against the
  deployment's own variables only, never the control plane's environment. Literal, multiline, JSON and secret values
  are not interpolated. Unknown references become empty and are logged by name.
- The env file is written with exact escaping (`KEY="..."`, `\`, `"` and `$` escaped). Variables that existed
  before this change keep the old quoting (`UsesLegacyEscaping`, cleared when the variable is saved again).
- `PreserveComposeNull` variables have no value and are left unset (not an empty string).
- Order is deterministic: defaults, then team, project, environment, server, application (page order `SortOrder`,
  then key), then preview copies; later definitions win. Preview copies only apply to previews.
- Defaults: `VESSEL_APPLICATION_ID`, `VESSEL_ENVIRONMENT_ID`, `VESSEL_SERVER_ID`, `VESSEL_BRANCH`,
  `VESSEL_PULL_REQUEST_ID` (previews) and `SOURCE_COMMIT` (runtime only) unless `IncludeDefaultVariables` is off;
  `PORT` (not for Compose) and `HOST=0.0.0.0` unless defined.

## Docker Compose policy (DEP-11, SEC-11)

The repository compose file is parsed and validated (never passed through):

- A missing or empty compose file fails the deployment; the path is relative to the base directory.
- `scale` or `deploy.replicas` greater than 1 is rejected (upstream v4.5.0 breaking change).
- `include:` is flattened; included files must be inside the repository (five levels at most); paths inside an
  included file resolve relative to it; a service or top-level name defined twice is an error.
- Host-level settings need an instance administrator as the deployment actor (webhook deployments without an actor
  never get them): `privileged`, host `network_mode`/`pid`/`ipc`/`uts`/`userns_mode`/`cgroup`, dangerous `cap_add`,
  `devices`, `gpus`, unconfined `security_opt`, privileged or host-network builds, absolute host bind mounts
  (including `/var/run/docker.sock`, reported as host-root equivalent) and volumes whose driver options bind host
  paths. Each allowed setting is written to the deployment log and audited (`deployment.compose_elevated`).
- Relative bind mounts must stay inside the repository (also through symbolic links); they point into the checkout,
  which is removed after the deployment, so Vessel warns to use named volumes. Anonymous and external volumes get
  warnings; external volumes are never created or removed by Vessel, and bind-mounted host paths are never deleted.
- Service, network and container names are validated. `BuildConfiguration.ComposeService` selects the public service.

## Runtime argument validation (SEC-10)

The Docker/Podman CLI client validates image references (distribution grammar), container and network names, compose
project names, labels and paths before they reach a command, and inserts `--` before positional names. Managed database
container names are validated as container names.
