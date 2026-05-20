# Phase 10: Reverse Proxy, Domains, TLS, and Routing

Phase 10 adds the first routing layer for deployed applications.

## Coolify Reference

The upstream Coolify default branch was inspected for proxy and domain behavior before implementation:

- `app/Actions/Proxy/GetProxyConfiguration.php`
- `app/Actions/Proxy/SaveProxyConfiguration.php`
- `app/Actions/Proxy/StartProxy.php`
- application `fqdn` handling in API, Livewire, and model code
- SSL certificate migrations and notification templates
- Coolify docs for domains, Traefik ACME, wildcard certificates, and dynamic proxy configuration

Vessel keeps the important product semantics: application domains are managed by the control plane, proxy configuration is generated deterministically, validation blocks unsafe config, apply/reload is auditable and protected by a lock, previous config is retained for rollback, and normal app TLS starts with Traefik ACME.

## Provider Choice

The MVP provider is Traefik.

Rationale:

- Coolify defaults to Traefik for automatic app routing and ACME.
- Traefik dynamic file configuration lets Vessel generate per-server route documents without editing application containers.
- The provider boundary keeps Caddy, Nginx, and custom providers possible later.

## Architecture

- `Vessel.Application.Proxy.IProxyProvider` defines generate, validate, apply, reload, and rollback operations.
- `Vessel.Infrastructure.Proxy.TraefikProxyProvider` implements the first provider and executes reload through `IProcessRunner`.
- `ProxyConfigurationService` owns locking, audit, version records, apply, and rollback orchestration.
- `DomainRoutingService` owns application domain routing commands.
- `CertificateManagementService` owns certificate status, issuance queueing, and renewal bookkeeping.
- Web controllers and Blazor pages call Application services only.

## Persistence

Phase 10 adds:

- `proxy_configuration_versions`
- `certificates`
- routing columns on `application_domains`: `TargetPort`, `TlsEnabled`, `Canonical`, `RedirectToCanonical`

The migration is `20260520091813_Phase10ProxyDomainsTls`.

## Operations

Traefik dynamic config is written under the application base directory:

```text
proxy/traefik/dynamic/server-{serverId}.yml
```

Reload currently sends `SIGHUP` to the `vessel-proxy` container through `IProcessRunner`. If the container is absent, the dynamic config write succeeds and the message explains that the proxy is not running. A failed reload attempts to restore the previous version.

TLS issuance uses `CertificateProvider.TraefikAcme` for the initial implementation. Traefik performs the ACME exchange when TLS routes are loaded with the `letsencrypt` resolver. Vessel stores certificate state and renewal due dates without persisting private keys for Traefik-managed certificates.

## Verification

Focused coverage includes:

- deterministic Traefik generated config
- duplicate-host and secret-like content validation
- failed reload rollback behavior
- EF model and migration coverage for proxy/certificate tables

