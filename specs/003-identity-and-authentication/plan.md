# Implementation Plan: Identity and Authentication (R0 scope + R1 Entra ID)

**Spec:** `specs/003-identity-and-authentication/spec.md` (v1.0, Approved) | **Branch:** `003-identity` | **Date:** 2026-11-04
**Shared base:** `docs/architecture/r0-solution-structure.md`, ADR-0004, ADR-0005

## Summary
R0 delivers: tenant-scoped user and external identity model, server-side sessions with immediate revocation, dev-only sign-in in a separate assembly with startup guard and CI artifact check, and the IdP configuration model. R1 adds the Entra ID OIDC flow on the same model. Google and SAML follow in R2 without model changes.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I Tenant boundary | Yes | `user`, `external_identity`, `session` are tenant-owned with RLS (ADR-0005, no global user table) |
| IV Secrets | Yes | IdP client secrets stored as secret-manager references only |
| V Dev auth isolation | Yes | `Identity.DevAuth` referenced only by `Api.DevHost`; guard + CI check |
| VI Audit | Yes | Sign-in outcomes and config changes |
| Others | Yes | |

## Data model (schema `identity`)
- `user`: `tenant_id`, `id`, `display_name`, `email`, `status`, `preferred_locale`, `created_via (jit, admin, scim)`.
- `external_identity`: `tenant_id`, `idp_config_id`, `issuer`, `subject`, `user_id`. Unique `(tenant_id, issuer, subject)`.
- `tenant_membership` lives in Tenancy (spec 001/002); Identity references it through contract. [Implementation note: membership status is read per request from a cached contract call.]
- `idp_config`: `tenant_id`, `id`, `protocol (oidc, saml)`, `kind (entra, google, generic)`, `issuer`, `client_id`, `secret_ref`, `jit_enabled`, `jit_domains text[]`, `group_role_map jsonb`.
- `verified_domain`: platform table, unique `domain` (one tenant per domain per installation). RLS exception.
- `session`: `tenant_id`, `id`, `user_id`, `created_at`, `last_seen_at`, `expires_at`, `revoked_at`, `roles_version`. Session store choice confirmed by ADR-0007 (default: this PostgreSQL table, with a short in-memory cache).

## Session design
- Cookie holds an opaque session ID only (secure, HTTP-only, SameSite=Lax).
- Each request validates session (cached up to 30 s for performance, but disable events evict cache immediately across instances via broker event, meeting "immediate" for disable).
- Role changes bump `roles_version`; claims reloaded within 5 minutes or on next event.

## Dev sign-in (R0)
- `Identity.DevAuth`: form posting a seeded username from `deploy/compose/seed/dev-users.json` (fictional data). No password hashing library, no password tables in any migration.
- `Api` startup: if `Auth:DevSignIn:Enabled` is true and environment is not Development/Test, throw at startup.
- CI: inspect the published production image; fail if `Identity.DevAuth.dll` exists or `/dev-signin` route is registered.

## Entra ID (R1)
- ASP.NET Core OpenID Connect handler per tenant, configured dynamically from `idp_config` (issuer validation pinned to the configured tenant issuer; no multi-tenant "common" issuer acceptance).
- Tenant discovery: by host (subdomain) first, else by email domain via `verified_domain`.
- Group mapping from `groups` claim; overage claim handled by Graph lookup later (R2) and logged.

## Tests
- Dev guard: production config with dev sign-in enabled fails startup (test host); artifact check script tested.
- Token validation: wrong issuer, wrong audience, expired, bad signature, missing nonce, replay (R1).
- Tenant isolation: same issuer+subject in tenant A and B creates two users; session cookie from A rejected on B's host.
- Disable membership: next request rejected; role change effective within 5 minutes.
- Open redirect and login CSRF.
