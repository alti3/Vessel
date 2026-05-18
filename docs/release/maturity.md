# Release Maturity

Vessel uses explicit maturity labels until stable releases begin.

## Alpha

Alpha releases are for early testers and contributors. APIs, schemas, commands, configuration, and workflows may change without compatibility guarantees.

Alpha quality requires:

- Architecture boundaries are in place.
- Core scaffold builds with the supported SDK.
- Critical security rules are documented.
- Known dangerous features are clearly marked incomplete or unavailable.

## Beta

Beta releases are for broader validation. Breaking changes are still possible but should be documented.

Beta quality requires:

- Core deployment workflows are usable.
- Auth, authorization, audit, and secret handling have focused tests.
- Installer and update paths have smoke coverage.
- Operational docs exist for supported install modes.

## Release Candidate

Release candidates are stable candidates for production-like validation.

Release candidate quality requires:

- No known critical or high severity security issues.
- Upgrade, rollback, backup, and restore paths have been validated.
- Full automated test suite passes.
- Public docs and troubleshooting guidance are current.

## Stable

Stable releases are intended for production use by supported operators.

Stable quality requires:

- Compatibility policy and support matrix are published.
- Release artifacts include checksums and, when available, signatures.
- Security reporting and vulnerability response processes are active.
- Operations, backup, restore, update, and migration guidance are complete.
