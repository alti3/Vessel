# Phase 13: Notifications and Event Delivery

Phase 13 adds reliable notification delivery inside the modular monolith.

## Behavior

- Notification events are persisted in `notification_events` with team ownership, optional user ownership, event type, severity, target context, payload JSON, delivery status, and failure metadata.
- In-app notifications are persisted in `in_app_notifications` and track unread, read, and archived state.
- External delivery attempts are persisted in `notification_delivery_attempts` with target, channel, attempt number, status, retry timestamp, provider message id, and safe failure reason.
- Notification targets remain team-owned in `notification_targets`; Phase 13 adds JSON configuration and keeps credentials in the existing encrypted secret vault through `CredentialsReferenceId`.
- Dispatch runs through `DispatchNotificationJob`, which delegates to `NotificationDispatchService`.
- Failed external deliveries are classified (NTF-03): only transient failures (HTTP 429/5xx, timeouts, network
  errors, SMTP 4xx) are retried, up to three attempts with exponential delays. Configuration and payload failures are
  final for that event.
- In-app delivery is written to PostgreSQL and published through the existing authorized notification SignalR hub.

## Channels

Implemented channel providers:

- In-app database notification center.
- Email through an SMTP server stored in the target secret JSON, or through the instance email settings with
  `{"to":"ops@example.com","useSystemSettings":true}` (NTF-06).
- Generic webhook POST with optional `X-Vessel-Signature` HMAC-SHA256 signing.
- Discord-compatible incoming webhook.
- Telegram bot `sendMessage`.
- Slack-compatible incoming webhook.

Secret JSON is write-only from the UI and is not returned by API or rendered back into components.

## Producers (NTF-01)

Vessel workflows raise notifications through the Application-level `INotificationEventPublisher`. The Infrastructure
implementation (`BackgroundNotificationEventPublisher`) redacts the content and enqueues `RaiseNotificationJob` in
Hangfire, which persists the event, publishes the in-app notification, and enqueues `DispatchNotificationJob`. The
publisher never touches the producer's DbContext and never throws: failures (including Hangfire being unavailable) are
logged and swallowed, so a notification problem can never fail a deployment, backup, or health poll.

| Event type | Producer | Severity |
| --- | --- | --- |
| `deployment.succeeded` / `deployment.failed` / `deployment.cancelled` | `DeploymentRunner` after the terminal state is saved | Info / Error / Warning |
| `backup.succeeded` / `backup.failed` | `ManagedDatabaseService.RunBackupExecutionAsync` | Info / Error |
| `backup.restore_failed` | `ManagedDatabaseService.RestoreAsync` | Critical |
| `server.unreachable` / `server.reachable` | `ServerHealthPollingService`, on status transitions only (repeated polls of a down server do not re-alert; recovery is announced only after an unreachable alert) | Critical / Info |
| `certificate.issuance_failed` / `certificate.renewal_failed` | `CertificateManagementService` (renewal alerts only on the transition into `Failed`) | Error |
| `proxy.apply_failed` | `ProxyConfigurationService` validation or apply failure | Error |

Messages are generic (resource names and IDs only); raw command output and exception text are never sent to external
channels. Relative resource links are made absolute from `Vessel:Host:PublicUrl` before delivery.

## Event catalog and per-event routing (NTF-05)

`NotificationEventCatalog` (Domain) lists every event key with its upstream Coolify toggle name (used by the 17.10
notification import mapping), category, default, "always deliver" flag, and throttle policy.
`GET /api/v1/notifications/event-types` returns it.

| Event key | Coolify toggle | Default | Throttle |
| --- | --- | --- | --- |
| `deployment.succeeded` / `deployment.failed` / `deployment.cancelled` | `deployment_success` / `deployment_failure` / none | off / on / off | none |
| `backup.succeeded` / `backup.failed` / `backup.missing` / `backup.restore_failed` | `backup_success` / `backup_failure` | off / on / on / on | failures: first, then daily until a success |
| `server.unreachable` / `server.reachable` | `server_unreachable` / `server_reachable` | on / off | once per incident; "reachable" only closes an alerted incident |
| `server.disk_usage_high` | `server_disk_usage` | on | daily per server |
| `server.ssh_host_key_changed` | `server_unreachable` | on | daily per server |
| `server.docker_cleanup_succeeded` / `server.docker_cleanup_failed` | `docker_cleanup_success` / `docker_cleanup_failure` | off / on | failure daily |
| `proxy.apply_failed` / `proxy.outdated` | none / `traefik_outdated` | on / on | hourly / daily |
| `application.status_changed` | `status_change` | off | 15 minutes per resource |
| `application.restart_limit_reached` | `restart_limit_reached` | on | hourly per resource (restart loops) |
| `scheduled_task.succeeded` / `scheduled_task.failed` | `scheduled_task_success` / `scheduled_task_failure` | off / on | failure daily until a success |
| `certificate.issuance_failed` / `certificate.renewal_failed` | `ssl_certificate_renewal` | always delivered | daily |
| `api_token.failing` | `api_token_failing` | always delivered | daily |
| `notification.channel_disabled` | `general` | always delivered, never by email | none |

- Each target stores explicit subscriptions in `SubscribedEvents` (`text[]`). `null` means the catalog defaults, which
  is how targets created before NTF-05 behave. `PUT /targets` accepts an optional `events` array; unknown keys are
  rejected. Always-delivered events ignore subscriptions and the minimum severity.
- The legacy `deploymentEvents` / `backupEvents` / `serverEvents` switches and `minimumSeverity` still apply on top.
- Unknown event types are never routed to external channels (they still reach the in-app center).
- Telegram targets accept `eventRoutes` (event key to numeric topic id); the route overrides the target's `threadId`.
- Producers for the newer keys (disk usage, host key, restart limit, scheduled tasks, API token failing, backup
  missing) belong to their own features; the catalog, routing, and throttling are ready for them.

## Throttling and dedupe (NTF-04)

`notification_throttles` holds one row per (team, event type, subject resource) with the last send time, equivalent to
upstream `NotificationThrottle`. `NotificationEventService.RaiseAsync` claims the row before persisting the event:
interval throttles renew with an atomic conditional `UPDATE ... WHERE SentAt < cutoff`, otherwise an
`INSERT ... ON CONFLICT DO NOTHING` on the unique index decides, so concurrent workers cannot both send. Once-per-incident
claims are released by the recovery event (`server.reachable`, `backup.succeeded`, and so on). A failed persist
releases the claim so the retried job is not throttled. `NotificationThrottlePurgeJob`
(`notifications.throttles.purge`, daily) deletes claims older than 30 days; an incident still open after that alerts
once more as a reminder. Not yet built: upstream's delayed "resource stopped unexpectedly" recheck job (it needs a
status-change producer).

## Delivery failure tracking (NTF-03)

- Providers classify responses like upstream `HandlesNotificationResponse`: Webhook 401/403/404/410, Discord
  401/403/404, Slack 403/404/410, Telegram 401/403/404 and 400 "chat not found" are configuration errors; 429 and 5xx
  are transient; other 4xx are payload rejections. A target blocked by the outbound policy, invalid credentials, a
  refused SMTP login, a TLS handshake failure, or a refused sender is a configuration error.
- `NotificationTarget.ConsecutiveFailureCount` counts configuration errors only. After three in a row the target is
  disabled with a safe `DisabledReason` (status only, never a response body) and `AutoDisabledAt`, and Vessel writes
  the `notification.target_auto_disabled` audit event, raises `notification.channel_disabled` (in-app plus the team's
  other enabled channels), and emails the team owners and admins through the instance email settings with the
  notification sender.
- A successful delivery or any settings change (including re-enabling) resets the counter. Test sends never count.

## Authorization and API (NTF-02 / IAM-02)

- New permissions: `notifications.read` (owners, admins, members) and `notifications.write` (owners and admins only,
  matching upstream `NotificationPolicy`). Token scope `read` maps to `notifications.read`, `write` to
  `notifications.write`. Both the Web policy and the Application services (`NotificationTargetService`,
  `NotificationCenterService`) enforce them.
- `GET /api/v1/notifications/targets`, `GET /in-app`, `POST /in-app/{id}/read|archive` require `notifications.read`.
- `PUT /api/v1/notifications/targets` and `POST /api/v1/notifications/targets/{id}/test` require `notifications.write`.
  The test endpoint sends a fixed-content `notification.test` event to that one target only.
- `POST /api/v1/notifications/events` was removed: events are raised only by Vessel workflows.
- Target responses include `hasCredentials`, never the secret. Secret JSON is stored in the encrypted secret vault and
  is write-only; omit it on update to keep the stored credentials.

## Outbound request policy (SSRF guard)

User-supplied webhook, Discord, Slack, and SMTP endpoints pass `IOutboundUrlPolicy` when saved and again when sent:

- `https` only by default; URLs with embedded credentials or a trailing-dot host are refused.
- The host is resolved; any answer in loopback, link-local/cloud metadata (169.254.0.0/16, fe80::/10, fd00:ec2::254,
  `metadata.google.internal`), multicast, documentation, or other reserved ranges is refused. Private ranges
  (RFC 1918, 100.64.0.0/10, fc00::/7) are refused unless allow-listed. IPv4-mapped, NAT64, and 6to4 forms are unwrapped.
- HTTP providers use a `SocketsHttpHandler.ConnectCallback` that resolves and connects only to allowed addresses
  (defeats DNS rebinding); redirects and environment proxies are disabled for these clients.
- SMTP (SEC-08, upstream `SafeSmtpHost`): the host must be a valid host name or IP; it is checked when saved and, at
  send time, `PinnedSocketConnector` resolves it exactly once, refuses the whole answer if any address is loopback,
  link-local/metadata, reserved, or a private range that is not allow-listed, and connects the socket only to the
  checked address. MailKit then speaks SMTP over that socket and verifies TLS against the original host name, so
  there is no DNS-rebinding window.
- Telegram bot tokens must match `digits:[A-Za-z0-9_-]{20,}` because the token is part of the request path.

Instance configuration (`Vessel:OutboundRequests`):

```json
{
  "Vessel": {
    "OutboundRequests": {
      "AllowHttp": false,
      "AllowLoopback": false,
      "AllowedInternalHosts": [ "alerts.corp.example", "*.svc.corp.example", "10.20.0.0/16" ]
    }
  }
}
```

Allow-listed hosts may also use plain `http`. Link-local/metadata and reserved ranges stay blocked even if an entry
overlaps them. This mirrors upstream `webhook_allowed_internal_hosts` / `webhook_allow_localhost`.

## Secret-safe URLs

Discord/Slack webhook paths and Telegram bot tokens are secrets. Notification HTTP clients remove the default
`IHttpClientFactory` loggers (which log the full URI), provider failures record only the exception type or HTTP status,
and OpenTelemetry `url.full` on those spans is reduced to `scheme://host[:port]` (request option set by the outbound
guard handler, plus an ancestor `SendNotification` activity tag as a second signal).

## Instance email (NTF-06) and auth email (IAM-10)

- `instance_email_settings` (singleton) holds the provider (`none`, `smtp`, `resend`; mutually exclusive), named SMTP
  servers (each with host, port, `starttls`/`tls`/`none`, username, encrypted password, sender, EHLO domain, timeout),
  the active server name, the encrypted Resend API key, `resend_from_address`/`resend_from_name` (v4.7.0 names), and
  the separate notification sender `notification_from_address`/`notification_from_name`. Secrets are protected with the
  secret key ring (`ISecretCipher`) and are write-only. Key-ring re-encryption does not yet rewrap these envelopes;
  keep old keys until the settings are saved again.
- API (instance administrators only; the Application service enforces it in addition to the policies):
  `GET /api/v1/settings/email` and `GET /api/v1/settings/email/presets` need `settings.manage`;
  `PATCH /api/v1/settings/email` and `POST /api/v1/settings/email/test` also need `secrets.write` (token scope
  `write:sensitive`). PATCH semantics: omitted fields are kept, an empty string clears a field or secret,
  `smtp_servers` replaces the list (a server keeps its password when `smtp_password` is omitted). `preset` fills host,
  port, encryption, and username from the 12 upstream presets.
- Transport: MailKit (`MailKitSmtpTransport`). `System.Net.Mail.SmtpClient` cannot do implicit TLS on 465 (Resend,
  Zoho presets), cannot set the EHLO domain, and cannot connect to a pre-checked address. MailKit is the established
  .NET SMTP library (MIT; it pulls MimeKit and BouncyCastle) and supports `SecureSocketOptions.StartTls`/`SslOnConnect`/
  `None`, `LocalDomain`, and `ConnectAsync(Socket, host, ...)`. It is referenced only by `Vessel.Infrastructure`, which
  satisfies the AGENTS.md dependency rule (clear need, no built-in alternative). Resend uses its fixed HTTPS API.
  SMTP replies and Resend bodies are never stored; SMTP 4xx is transient, 530/534/535/538 and a refused sender are
  configuration errors, and other 5xx reject only the message.
- Auth email: password reset, team invitation, and email verification (registration of every user after the first, or
  `POST /api/auth/email-verification/send`) are rendered (text and HTML), encrypted with the key ring, and queued as
  `SendInstanceEmailJob` (Hangfire retries transient failures; permanent ones are audited as `email.delivery_failed`).
  Links come from `Vessel:Host:PublicUrl` only; without it, or without instance email, nothing is queued and the
  response is unchanged (no account enumeration). Landing pages need a click (POST) before a token is redeemed:
  `/password-reset?email=...&token=...`, `/invitations/accept?token=...`, `/verify-email?token=...`. Verification
  tokens are stateless, authenticated, bound to the user id and email address, and expire after 24 hours.

## Coolify Reference

Inspected upstream Coolify notification areas from the current default branch on 2026-06-04:

- `app/Notifications/*`
- `app/Notifications/Channels/*`
- `app/Jobs/SendMessageToDiscordJob.php`
- `app/Jobs/SendMessageToSlackJob.php`
- `app/Jobs/SendMessageToTelegramJob.php`
- `app/Jobs/SendWebhookJob.php`
- `app/Livewire/Notifications/*`
- `resources/views/livewire/notifications/*`
- notification settings migrations for email, Discord, Telegram, Slack, Pushover, and webhook settings

Wave 2 (NTF-03/04/05/06, IAM-10, SEC-08) additionally consulted, on 2026-11-20: `app/Traits/TracksDeliveryFailures.php`,
`app/Traits/HandlesNotificationResponse.php`, `app/Traits/HasNotificationSettings.php` (always-send events),
`app/Models/NotificationThrottle.php`, `app/Contracts/ThrottledNotification.php`, `app/Models/Server.php`
(unreachable/reachable incident handling), `app/Notifications/**` (throttle intervals and toggles),
`app/Jobs/SendMessageTo*Job.php`, `app/Support/SmtpPresets.php`, `app/Support/SmtpTransportFactory.php`,
`app/Rules/SafeSmtpHost.php`, `app/Traits/HasSmtpServers.php`, and
`app/Http/Controllers/Api/InstanceEmailSettingsController.php`.

Coolify models notification settings per channel and exposes event toggles for deployment success/failure, status changes, backups, scheduled tasks, Docker cleanup, server disk/reachability/patch events, and proxy outdated checks. Vessel preserves the important operator behavior with a generic event, target, attempt, and provider model instead of one settings table per channel.

## Verification

- `dotnet build src\Vessel.Web\Vessel.Web.csproj --no-restore --artifacts-path artifacts\phase13-build`
- `dotnet test tests\Vessel.UnitTests\Vessel.UnitTests.csproj --no-restore --artifacts-path artifacts\phase13-build --verbosity minimal --filter "Phase13"`

Notes:

- EF tooling again reported that `dotnet-ef` `10.0.8` is older than the installed .NET 11 preview runtime.
- The repository moved to the stable .NET 11 SDK (`11.0.100`) on 2026-11-20; see `docs/development/dotnet-sdk.md`.
