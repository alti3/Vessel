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
