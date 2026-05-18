# ADR-0008: Custom Identity Model with Argon2id Password Hashing

## Status

Accepted

## Context

Phase 4 requires email/password authentication, tokens, team-aware authorization, 2FA, and audit events. Vessel already has domain-owned users and team memberships, and the domain must not couple to ASP.NET Core Identity types.

Coolify's current implementation uses Laravel Fortify for browser auth and Sanctum for API tokens. The relevant behavior to preserve is:

- users own a personal team by default
- the active team determines team-scoped permissions and token scope
- API tokens are shown once, stored as hashes, scoped, expirable, and revocable
- 2FA state and recovery material are never returned in normal user payloads
- login, token, and team membership changes are security-relevant audit events

## Decision

Vessel uses a custom identity model built on the existing `User`, `Team`, and `TeamMembership` domain model instead of ASP.NET Core Identity.

Passwords are hashed through `IPasswordHasher`, implemented in Infrastructure with `Konscious.Security.Cryptography.Argon2.Argon2id`. Vessel does not use the ASP.NET Core Identity built-in password hasher.

ASP.NET Core authentication is still used for transport concerns:

- secure HTTP-only cookies for browser sessions
- a custom bearer-token handler for API tokens
- policy-based authorization using explicit Vessel permissions

## Consequences

The Application layer owns auth use cases and security decisions. Infrastructure owns hashing and persistence details. Web controllers and auth handlers stay thin and delegate to Application services.

Future OIDC, GitHub, and GitLab login flows should link external identities into the existing `User.ExternalSubject` boundary rather than replacing the domain user model.
