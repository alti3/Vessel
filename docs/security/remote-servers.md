# Remote (SSH) Servers

Vessel manages remote servers over OpenSSH. All remote work — deployments, health polling, proxy lifecycle and file delivery, validation, key rotation and terminals — goes through one path: `IServerCommandRunner` (`ServerCommandExecutor`) with connections resolved by `IServerSshConnectionFactory`. External commands always run through `IProcessRunner` with structured arguments; remote commands are POSIX-quoted argument vectors, never interpolated strings.

## Remote runtime model

The runtime CLI runs **on the server**: Vessel executes `ssh … -- user@host 'docker' 'compose' …`. Vessel does not use `DOCKER_HOST=ssh://`, because the Docker CLI's own `ssh` invocation cannot carry Vessel's fixed options, per-server known_hosts file or team key. `ContainerRuntimeTarget.Remote` routes `IContainerRuntimeClient` calls for SSH servers to the CLI over SSH; the control-plane runtime is never consulted for an SSH server.

Deployments to SSH servers prepare the workspace locally (checkout, rendered compose file, `.env`), upload it as a tar stream over the SSH connection to `{Vessel:ServerFiles:RemoteRootDirectory}/deployments/<id>` (default `/var/lib/vessel`, unpacked under `umask 077`, files 0600, symbolic links that leave the workspace are dropped), run `docker build` / `docker compose` there, run the health check on the server (`curl` or `wget`), and remove the uploaded directory afterwards. Repository compose paths are validated against the local checkout and rewritten to the uploaded copy. Managed databases/services on SSH servers are not covered yet (they still fail closed).

## SSH keys

- Team-owned `SshKey` records (`/api/v1/security/keys`): private material is stored only in the secret vault; APIs return the public key, algorithm, bits and SHA-256 fingerprint, never the private key.
- Import accepts unencrypted OpenSSH/PEM keys only (no PuTTY, no DSA, RSA ≥ 2048 bits, one key block, ≤ 16 KiB); the public key is derived with `ssh-keygen -y`. `POST /api/v1/security/keys/generate` creates Ed25519 keys.
- A server may only use a key of its own team. A key used by a server cannot be deleted (HTTP 409).
- `POST /api/v1/security/keys/{id}/rotate` (background job): for every SSH server on the old key, append the new public key to `~/.ssh/authorized_keys` over the old key, log in with the new key, switch the server, then remove the old key line. Servers whose new key cannot log in keep the old key (the added line is undone).
- During a connection the decrypted key exists only as a 0600 file under `{Vessel:Ssh:RuntimeDirectory}/keys` (default `{AppContext.BaseDirectory}/storage/ssh`; host installs should use `/var/lib/vessel/ssh`). Files are kept while connections use them plus a 2-minute idle window, then deleted; leftovers are purged at startup.

## Fixed connection options

Every `ssh`/`scp` invocation uses `-F /dev/null`, `BatchMode=yes`, `IdentitiesOnly=yes`, `PasswordAuthentication=no`, `KbdInteractiveAuthentication=no`, `ClearAllForwardings=yes`, `ForwardAgent=no`, `ForwardX11=no`, `PermitLocalCommand=no`, `UpdateHostKeys=no`, `ConnectTimeout` (default 15 s), `ServerAliveInterval` (default 15 s), `ServerAliveCountMax=3`, `LogLevel=ERROR`, and `--` before the destination. Host and user are validated so they can never be read as options. `scp` takes the port with `-P`.

## Host key pinning

- The first connection pins the host key the server presents (trust on first use): `ssh` with every login method disabled and `StrictHostKeyChecking=accept-new` against an empty scratch file.
- Later connections verify against a Vessel-managed known_hosts file per server (`known_hosts/known_hosts_<server>_<hash>`, 0600, regenerated from the database) with `HostKeyAlias=vessel-server-<id>`; global and user known_hosts files are ignored.
- Mode (`Vessel:Ssh:HostKeyChecking` = `enforce` (default) | `warn` | `off`; per server `hostKeyChecking`): `enforce` refuses a changed key, `warn` connects and reports it, `off` skips checking. New servers start at the instance mode. An instance-wide `off` disables checking everywhere; making a server looser than the instance mode requires an instance administrator.
- When OpenSSH reports a changed key for this server's alias, Vessel scans the presented key (rate-limited, each scan is a failed login), stores it as pending, audits `server.ssh_host_key_changed`, and raises the `server.host_key_changed` notification (fingerprints only).
- `POST /api/v1/servers/{id}/host-key/trust` pins the pending key after out-of-band verification; `POST /api/v1/servers/{id}/host-key/reset` forgets the pin. Changing a server's host or port also forgets it.

## Validation and lifecycle

- `POST /api/v1/servers/{id}/validate` (POST only) queues a validation job behind a per-server lock: SSH reachability (pins the host key), `/etc/os-release` (`ID`/`ID_LIKE` derivatives), package manager, login user and passwordless sudo, runtime presence and version (Docker Engine ≥ 24, Podman ≥ 4), Compose version. Results are stored on the server (`validation_*` columns).
- Docker is installed only with `{"installDocker": true}` and only when the login user is root or has passwordless sudo (Debian/RHEL/SUSE families via `get.docker.com`, Alpine `apk`, Arch `pacman`). Otherwise the result carries install guidance.
- `GET/PATCH/DELETE /api/v1/servers/{id}`. Delete is refused (HTTP 409) while applications, databases, services, deployments or server-scoped variables use the server.
