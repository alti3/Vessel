# Coolify service templates

The `*.yaml` files in this folder are copied unchanged (header comments included) from the Coolify project,
`templates/compose/` (https://github.com/coollabsio/coolify), and are licensed under the Apache License 2.0.
The full license text is in [LICENSE-coolify.txt](./LICENSE-coolify.txt).

Copyright (c) Andras Bacsai and the Coolify contributors.

Vessel imports them as data (TPL-01, see `docs/service-templates.md`): it does not modify the files; at import it
removes host port publishing, `container_name` and `coolify.*` labels and resolves the magic variables per service
instance. Refresh a file by copying the newer upstream version over it.
