# Secrets

Vessel stores secret metadata separately from encrypted payloads.

- `secret_references` stores ownership, scope, key, provider, policy, and provider reference.
- `secret_values` stores AES-GCM ciphertext, nonce, tag, and key version.
- API and UI list flows return masked values for secret environment variables.
- Secret reveal requires `secrets.read` and is audited.
- Secret create/update paths require `secrets.write`.

Configure production secret encryption with:

```json
{
  "Secrets": {
    "MasterKey": "<base64-encoded-32-byte-key>",
    "KeyVersion": "v1"
  }
}
```

The current Phase 7 rotation plan records `KeyVersion` on each encrypted value. A later hardening phase should add a re-encryption job that writes new `secret_values` with a new key version while never logging plaintext.
