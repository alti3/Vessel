# Secrets

Vessel stores secret metadata separately from encrypted payloads.

- `secret_references` stores ownership, scope, key, provider, policy, and provider reference.
- `secret_values` stores AES-GCM ciphertext, nonce, tag, and key version.
- API and UI list flows return masked values for secret environment variables.
- Secret reveal requires `secrets.read` and is audited.
- Secret create/update paths require `secrets.write`.

## Key ring

Secrets are encrypted with AES-256-GCM. Every ciphertext records the key version that produced it (`secret_values.KeyVersion`, and the `vsec1:<version>:...` envelope for TOTP secrets), and decryption selects the key by that version.

```json
{
  "Secrets": {
    "KeyVersion": "v2",
    "MasterKey": "<base64 32-byte key for v2>",
    "KeyFile": null,
    "Keys": { "v1": "<previous base64 key>" }
  }
}
```

- The active key for `KeyVersion` comes from `MasterKey`, else `KeyFile` (a file containing the base64 key, e.g. a systemd credential; it must not be accessible by other users), else `Keys:<KeyVersion>`.
- **There is no built-in fallback key.** Outside `Development` startup fails when no active key is configured. An unset environment is treated as `Production`.
- In `Development` only, Vessel generates a random per-install key once and persists it with mode `0600` at `Secrets:DevelopmentKeyFile` (default `<LocalApplicationData>/Vessel/secrets/development-master.key`).
- Generate a key with `openssl rand -base64 32`.

## Rotation and re-encryption

1. Move the current key into `Secrets:Keys:<old-version>`, set a new `KeyVersion` and active key, restart.
2. Run `POST /api/v1/admin/secrets/reencrypt?dryRun=true` (instance administrator, `settings.manage`) to count values that use an old key and verify they decrypt.
3. Run `POST /api/v1/admin/secrets/reencrypt` to queue the `SecretReencryptionJob`. It re-encrypts `secret_values` and TOTP secrets in batches under an exclusive lock, is idempotent, and audits counts only (`secrets.reencrypted`); plaintext is never logged.
4. Remove the old key from `Secrets:Keys` only after a run reports zero undecryptable values.

This mirrors Coolify's `app-key:reencrypt`.

### Upgrading alpha installs that ran without a key

Earlier alpha builds silently used a public built-in key when `MasterKey` was empty. To migrate, configure a real key, set `Secrets:AllowLegacyBuiltInKeyDecryption=true` (decryption only, never used to encrypt), run the re-encryption command, then remove the flag.

## TOTP secrets

TOTP shared secrets are stored encrypted with the same key ring (purpose-bound associated data per user). Legacy plaintext values remain readable and are upgraded on the next successful 2FA login or by the re-encryption command.
