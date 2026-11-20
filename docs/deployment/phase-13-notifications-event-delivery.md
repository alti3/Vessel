# Phase 13: Notifications and Event Delivery

Phase 13 adds reliable notification delivery inside the modular monolith.

## Behavior

- Notification events are persisted in `notification_events` with team ownership, optional user ownership, event type, severity, target context, payload JSON, delivery status, and failure metadata.
- In-app notifications are persisted in `in_app_notifications` and track unread, read, and archived state.
- External delivery attempts are persisted in `notification_delivery_attempts` with target, channel, attempt number, status, retry timestamp, provider message id, and safe failure reason.
- Notification targets remain team-owned in `notification_targets`; Phase 13 adds JSON configuration and keeps credentials in the existing encrypted secret vault through `CredentialsReferenceId`.
- Dispatch runs through `DispatchNotificationJob`, which delegates to `NotificationDispatchService`.
- Failed external deliveries are retried up to three attempts with exponential delays.
- In-app delivery is written to PostgreSQL and published through the existing authorized notification SignalR hub.

## Channels

Implemented channel providers:

- In-app database notification center.
- Email through SMTP settings stored in target secret JSON.
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
channels. The `deployment.`, `backup.`, and `server.` prefixes map to the per-target event toggles.

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
- SMTP cannot pin the connection address, so the host is re-checked before each send (narrows, but does not close,
  the rebinding window).
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

Coolify models notification settings per channel and exposes event toggles for deployment success/failure, status changes, backups, scheduled tasks, Docker cleanup, server disk/reachability/patch events, and proxy outdated checks. Vessel preserves the important operator behavior with a generic event, target, attempt, and provider model instead of one settings table per channel.

## Verification

- `dotnet build src\Vessel.Web\Vessel.Web.csproj --no-restore --artifacts-path artifacts\phase13-build`
- `dotnet test tests\Vessel.UnitTests\Vessel.UnitTests.csproj --no-restore --artifacts-path artifacts\phase13-build --verbosity minimal --filter "Phase13"`

Notes:

- EF tooling again reported that `dotnet-ef` `10.0.8` is older than the installed .NET 11 preview runtime.
- The repository still uses SDK `11.0.100-preview.4.26230.115`; replace with stable .NET 11 after GA per project requirements.
