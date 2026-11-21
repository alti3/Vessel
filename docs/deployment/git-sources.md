# Git sources (private repositories)

Status: DEP-08 and the git half of SEC-10 (Wave 2). This page describes how Vessel clones private repositories and
validates git input. Coolify reference: `app/Models/GithubApp.php`, `GitlabApp.php`, `GiteaApp.php`,
`bootstrap/helpers/github.php` (`githubApiUrlFromHtmlUrl`, `generateGithubToken`) and
`bootstrap/helpers/applications.php` (`GIT_PINNED_HOST_KEYS`).

## Source kinds

| Kind | Clone transport | Credential used per clone | Secrets in the vault |
| --- | --- | --- | --- |
| `GitHubApp` | HTTPS | Installation token minted per clone from an RS256 JWT (app id + private key) | app private key, webhook secret |
| `GitLabApp` | HTTPS (`oauth2`) | OAuth access token, refreshed before expiry with the refresh token | access/refresh token, client secret, webhook secret |
| `GiteaApp` (Gitea, Forgejo, Codeberg) | HTTPS (`oauth2`) | Same as GitLab | same as GitLab |
| `DeployKey` | SSH | A team SSH key marked *git-related* (`SshKey.IsGitRelated`) | the SSH key's private key (already in the vault) |
| `HttpsCredential` | HTTPS | Username + password or access token | password/token |

* GitHub Enterprise: the API URL is derived from the HTML URL — `https://api.github.com` for github.com,
  `https://api.<host>` for `*.ghe.com`, `<origin>/api/v3` for GitHub Enterprise Server — unless `apiUrl` is given.
  GitLab uses `<html>/api/v4`, Gitea/Forgejo `<html>/api/v1`; self-hosted URLs may have a path prefix.
* Presets: `github`, `gitlab`, `gitea`, `codeberg`, `bitbucket`. Provider URLs must be plain `https://` URLs.
* `organization` (comma separated) limits a source to repositories of those owners.
* Provider REST calls (token minting, refresh, repository/branch listing) go through the SSRF-guarded outbound
  handler. Self-hosted providers on private networks must be on the outbound allow-list. GitHub requests are retried
  on 5xx; a rejected JWT reports clock skew from the response `Date` header.
* The interactive OAuth authorization-code flow is not implemented yet: connect GitLab/Gitea sources by supplying an
  access token (and optionally the refresh token, client id and client secret for automatic refresh).

## Ownership and authorization

* Sources belong to a team. Creating, updating or deleting a team source requires **team-wide**
  `applications.write` (a scoped grant is not enough); reading requires `applications.read` somewhere in the team.
* `isSystemWide` app sources (GitHub/GitLab/Gitea apps only) are usable by every team; creating, changing or deleting
  them requires an instance administrator (and a `root` token for API calls). Their secrets stay in the owning team's
  vault scope and are never visible to other teams.
* Deploy keys must be SSH keys of the source's own team marked git-related; deploy key and HTTPS sources can never be
  system-wide.
* A source used by applications cannot be deleted; an SSH key used by a deploy key source cannot be deleted.

## Host binding

A source only ever sends its credential to its own provider. `GitSourceConnection.CheckRepository` refuses a
repository unless it is HTTPS on exactly the source host, port and path prefix (token sources) or SSH (deploy keys,
on the source host when one is set), and its owner passes the organization filter. The check runs when a source is
selected, when a source is edited (applications that would no longer match block the edit) and before every clone.

## How credentials reach git

`IGitCredentialProvider` resolves short-lived credentials; `GitProcessClient` materializes them per operation in a
private `0700` temporary directory that is deleted afterwards (`GitAuthenticationScope`):

* HTTPS: `GIT_ASKPASS` points to a helper script that reads the username/token from the git process environment and
  answers **only** prompts for `https://<bound host>`; a submodule or redirect to another host gets no answer.
  Host credential helpers are disabled (`credential.helper=` via `GIT_CONFIG_*`), transports are limited to
  `https:ssh` (`GIT_ALLOW_PROTOCOL`), and the token is added to the process redaction profile. Tokens are never put in
  the repository URL, the command line, `.git/config` or deployment logs.
* SSH: the deploy key is written as a `0600` identity file and used with `IdentitiesOnly=yes`.

Repository URLs with embedded passwords are rejected at the API/UI boundary.

## SSH host keys

Every SSH git connection uses a dedicated `known_hosts` file:

1. github.com / ssh.github.com:443, gitlab.com / altssh.gitlab.com:443 and bitbucket.org / altssh.bitbucket.org:443
   accept only the provider-published keys (`GitHostKeys.Published`).
2. Other hosts use the team's pinned keys (`git_known_hosts`) with `StrictHostKeyChecking=yes`.
3. A host with no pin is trusted on first use (`accept-new`); the recorded key is pinned for the team and enforced
   afterwards. Pins are per team, so one team's first connection never decides what another team trusts.

Operators can list keys (`GET /api/v1/sources/known-hosts`), pin a verified key (`POST`, replaces a pin of the same
key type, e.g. after a host key rotation) or remove a pin (`DELETE /api/v1/sources/known-hosts/{id}`) so the next
connection pins again.

## API

| Method | Route | Notes |
| --- | --- | --- |
| GET | `/api/v1/sources` | Team sources plus system-wide sources; secrets reported only as `has*` flags |
| POST | `/api/v1/sources` | `CreateGitSourceRequest` (kind-specific fields) |
| GET/PATCH/DELETE | `/api/v1/sources/{id}` | Non-empty secret fields replace stored secrets |
| GET | `/api/v1/sources/{id}/repositories?page=` | App sources: installation / membership / user repositories |
| GET | `/api/v1/sources/{id}/branches?repository=` | `owner/name` (app sources, provider API) or a repository URL (`git ls-remote` with the source credentials) |
| PUT | `/api/v1/applications/{id}/source` | `{ "sourceId": "…" }` selects a source, `null` clears it |

`POST /api/v1/applications` accepts an optional `sourceId`. `GET /api/v1/applications/{id}/git/refs` lists refs
with the application's source credentials.

## Webhooks

App sources sign deliveries with their webhook secret. The existing `/webhooks/github`, `/webhooks/gitlab` and
`/webhooks/gitea` endpoints verify a delivery for an application with the application's own webhook secret **or** the
webhook secret of the application's source, only when the source kind matches the provider. GitLab system hooks are
not implemented yet.

## Boundary validation (SEC-10)

* `RepositoryUrl`: only `https://`, `http://`, `ssh://` and SCP-like `user@host:path`; no whitespace or control
  characters, no leading `-` (URL, user, host or path segment), no dot segments, query or fragment, no `file://` or
  `ext::` transports.
* `GitSource.FromInput` validates the URL, branch (`GitRef`) and pinned commit at the API/UI boundary.
* Webhook branches that are not valid git refs never match an application; refs offered by listing endpoints are
  filtered through `GitRef`.
* The deployment queue validates the application branch and requested commit/rollback ref for every trigger, and the
  runner validates them again at start (`GitSource.EnsureSafeForGit`). Git positional arguments follow `--`.
