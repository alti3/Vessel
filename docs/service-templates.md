# Service templates (Coolify catalog format)

Status: TPL-01 (Wave 2). Vessel's catalog combines the built-in templates (`source: "vessel"`, keys unchanged) with
templates in Coolify's compose format (`source: "coolify"`, key `coolify-<file name>`), embedded from
`src/Vessel.Application/ManagedServices/Templates/Coolify/`. Coolify reference: `templates/compose/*.yaml`,
`bootstrap/helpers/parsers.php`, `bootstrap/helpers/shared.php` (`generateEnvValue`), `bootstrap/helpers/services.php`.

## File format

A template is a Compose file whose leading comment block carries metadata:

```yaml
# documentation: https://ghost.org
# slogan: Ghost is a content management system (CMS) and blogging platform.
# category: cms
# tags: cms, blog, content
# logo: svgs/ghost.svg
# port: 2368

services:
  ghost:
    image: ghost:6
    environment:
      - SERVICE_URL_GHOST_2368
      - database__connection__password=${SERVICE_PASSWORD_MYSQL}
```

Recognized headers: `documentation`, `slogan`, `category`, `tags`, `logo`, `port`, `minversion`, `env_file`,
`amd_only`, `arm_only`, `ignore`, `outdated` (`ignore: true` templates are not imported). The summary returned by
`GET /api/v1/service-templates` (and `GET /api/v1/service-templates/{key}`) exposes the metadata, import `warnings`
and the routable `endpoints` (Compose service + port).

## Magic variables

| Variable | Value |
| --- | --- |
| `SERVICE_URL_<NAME>[_<PORT>]` | Public URL of the service domain routed to Compose service `<name>` (port `<PORT>`, else the `# port:` header); falls back to `http://<service>:<port>` when no domain exists |
| `SERVICE_FQDN_<NAME>[_<PORT>]` | Same host (and path) without the scheme |
| `SERVICE_USER_<NAME>` / `SERVICE_LOWERCASEUSER_<NAME>` | 16 random letters/digits (lower case) |
| `SERVICE_PASSWORD_<NAME>` / `SERVICE_PASSWORD_64_<NAME>` | 32 / 64 random letters and digits |
| `SERVICE_PASSWORDWITHSYMBOLS[_64]_<NAME>` | 32 / 64 characters including `.env`-safe symbols |
| `SERVICE_BASE64[_32,_64,_128]_<NAME>` | Random letters/digits of that length (upstream semantics: not Base64) |
| `SERVICE_REALBASE64[_32,_64,_128]_<NAME>` | Base64 of that many random bytes |
| `SERVICE_HEX_32/64/128_<NAME>` | Random lower-case hex |

Generated values use a cryptographic RNG, are created **once per service instance**, stored as one JSON secret in the
vault (`ServiceResource.GeneratedSecretsReferenceId`) and reused on every re-provision; only variables added by a newer
template version are generated later. Secret template inputs are stored in the same vault entry, never in the
service configuration JSON. The Supabase JWT commands are not supported. Other `${VAR}` / `${VAR:-default}` references
become optional inputs (required for `${VAR:?}`) with the template default.

## Import safety rules

At import the Compose document is normalized with YamlDotNet (`YamlServiceTemplateComposeProcessor`):

* host `ports:` publishing and `container_name` are removed (routing goes through the proxy and service domains);
* `coolify.*` labels are dropped; `SERVICE_*` declarations become `KEY=${KEY}` so values come from the generated
  `.env` file;
* `scale`/`replicas` greater than 1, `build`, host bind mounts, host namespaces and privileged services are rejected;
  YAML anchors, aliases and merge keys are rejected;
* environment variable names passed through the `.env` file must be upper case.

Templates that violate a rule are reported as import errors and are not listed (two upstream templates, `forgejo`
and `memos`, were left out for that reason).

## Image pinning

Every image must be pinned. Untagged images, `:latest` or other floating tags and interpolated image references are
rejected. An image pinned only to a major version (for example `ghost:6`) is accepted with a warning on the template
summary.

## Curated upstream set and license

The embedded `*.yaml` files are copied unchanged (header comments included) from Coolify `templates/compose/` and are
licensed under the Apache License 2.0; see `Templates/Coolify/NOTICE.md` and `LICENSE-coolify.txt`. To refresh a
template, copy the newer upstream file over it; its content hash becomes the template version.
