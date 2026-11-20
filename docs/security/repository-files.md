# Repository Files and Owned Paths

Cloned repositories are untrusted input. A repository can commit symbolic links (for example `docker-compose.yml -> /etc/vessel/appsettings.Production.json`), so lexical path checks are not sufficient.

## Rules

- `IPathSafetyService.EnsureOwnedPath` / `EnsureOwnedRelativePath` check containment lexically **and** after resolving every existing symbolic link (intermediate directories and the final component, with `..` applied physically). Anything that resolves outside the real owned root is rejected.
- `IPathSafetyService.EnsureNoSymlinks` additionally rejects any symbolic link below the root. It is used for repository reads and for every file Vessel writes into a workspace.
- Compose files and Dockerfiles read from a cloned repository must be regular files with no symbolic link on their path. On Linux the opened descriptor is re-checked through `/proc/self/fd` to defeat a link swapped in after validation. Reads are capped at 2 MiB.
- Deployment and managed-service workspace files are written atomically: content goes to a new temporary file in the target directory (created `0600` when owner-only), then is renamed over the destination. Writes never follow a symlinked destination or parent.
- Deployment file paths (`DockerfilePath`, compose path) are validated by segment: `..` segments and null characters are rejected; `./` segments are ignored.
- Compose content that references parent directories through a `../` path segment is rejected. Unrelated `..` text (version ranges, ellipses) is allowed.

Upstream reference: Coolify `confinePathToBase()` / `validateShellSafePath()` and `AtomicFilesystem` (v4.4.0 #11911/#11943/#11944/#11993, v4.5.1 #12324).
