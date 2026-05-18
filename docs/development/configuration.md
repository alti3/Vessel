# Local Configuration

Vessel uses the standard ASP.NET Core configuration order:

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. environment variables
4. future secret stores and database-backed settings

Supported environment names are:

- `Development`
- `Staging`
- `Production`
- `Testing`

Unknown environment names fail startup so deployment behavior stays predictable.

## Non-Secret Settings

The `Vessel` configuration section contains host, diagnostics, external dependency, security header, and rate-limit settings. Local overrides should use `appsettings.Development.json` or environment variables.

Example environment variable form:

```powershell
$env:Vessel__Database__Enabled = "true"
$env:Vessel__Database__ConnectionString = "Host=localhost;Port=5432;Database=vessel;Username=vessel;Password=change-me"
$env:Vessel__Redis__Enabled = "true"
$env:Vessel__Redis__ConnectionString = "localhost:6379"
```

Do not commit real passwords, tokens, object storage keys, OAuth secrets, or webhook secrets.

## Health Endpoints

Vessel exposes:

- `/live`: lightweight process liveness
- `/ready`: readiness for enabled PostgreSQL, Redis, Hangfire storage, and object storage checks
- `/health`: aggregate health report

PostgreSQL, Redis, Hangfire storage, and object storage checks are disabled by default in the base settings until those services are configured.

## Diagnostics

Serilog is the structured logger. Logs include service, environment, correlation ID, and trace context when available.

OpenTelemetry tracing is enabled by default for ASP.NET Core requests and outgoing HTTP calls. Set `Vessel:Diagnostics:OtlpEndpoint` to an absolute OTLP endpoint URI to export traces.

## Security Baseline

Production and staging enable secure headers by default. Development and testing disable them to avoid local tooling friction.

Named rate-limit policies are registered for future auth, webhook, public API, and terminal endpoints:

- `auth`
- `webhooks`
- `api`
- `terminal`
