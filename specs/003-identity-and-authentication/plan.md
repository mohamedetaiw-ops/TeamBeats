# Implementation Plan: Identity and Authentication (R0 scope, R1 Entra ID, R2 Google, SAML and AD FS)

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
- `user`: `tenant_id`, `id`, `display_name`, `email`, `status`, `preferred_locale`, `created_via (jit, admin, scim, directory)`.
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

## Directory connection and sync (R1 Entra ID, R2 on-premises AD and AD FS; starts after US5, US6 and FR-011 to FR-015 are approved)
- Data model additions (schema `identity`): `directory_connection` (`tenant_id`, `id`, `kind (entra, adds)`, `entra_tenant_id` or `ldaps_endpoint` and `base_dn`, `secret_ref`, `user_scope jsonb`, `group_scope jsonb`, `attribute_map jsonb`, `schedule_minutes`, `deletion_threshold_pct`, `status`); `directory_user_link` (`tenant_id`, `connection_id`, `directory_id` (Entra `oid` or AD `objectGUID`), `user_id`, `disabled_by_directory`); `directory_group_link` (`tenant_id`, `connection_id`, `directory_id`, `group_id`); `user.owner_fields text[]` for directory-owned fields.
- Entra ID: Microsoft Graph with application permissions `User.Read.All`, `Group.Read.All`, `AdministrativeUnit.Read.All` only; delta queries with the delta token stored as a reference per connection. The OIDC link key becomes issuer plus `oid` for Entra ID, so a synced user and a signed-in user meet on the same `external_identity` row (`subject = oid`).
- On-premises AD DS: LDAPS only (plain LDAP refused at save), `uSNChanged` incremental reads, through the connector framework (spec 017). SaaS tenants use an outbound-only sync agent; on-premises installs may connect directly. AD FS sign-in is an `idp_config` of kind `adfs` with subject `objectGUID`.
- Sync job on the outbox worker with tenant context; idempotent per directory ID; threshold check before applying disables; failures leave data unchanged and alert after 3 consecutive failures.
- Leaver: `disabled_by_directory = true` disables the tenant membership, revokes sessions through the existing revocation path and publishes `UserDeprovisioned`. Product admins cannot re-enable while the flag is set (FR-013).
- `IDirectoryReader` contract is published for spec 002 organization mapping and spec 010 profile attributes.
- Tests: create, rename, disable, re-enable, scope change; first sign-in links to the synced user with JIT on; no duplicate users; LDAP without TLS refused; write permission detected at test connection gives a warning; threshold breach stops the run; directory IDs from one connection never link identities from another; AD FS token without `objectGUID` refused; a sync for tenant A never writes tenant B rows.

## R2: Google, AD FS and SAML sign-in, SCIM extension point (US4, US5, FR-005, FR-008, FR-015)
On-premises AD DS sync over LDAPS through the sync agent shipped with spec 017 T015 (PR #154), and Google Workspace directory reads with spec 017 T019. R2 adds the sign-in side on the same `idp_config` and `external_identity` model, so authorization does not change (FR-005).

- **One provider per tenant, kind decides the flow.** `idp_config.kind` is `entra`, `google` or `adfs` for OIDC; SAML 2.0 adds `protocol = saml` in slice 2. The admin page picks the kind; the sign-in page names the provider by kind.
- **Google OIDC.** Issuer is exactly `https://accounts.google.com` and the client ID a Google OAuth client. Every Google account shares that issuer, so the provider lists the tenant's hosted domains (`idp_config.hosted_domains`, at least one) and the token's `hd` claim must be one of them; a personal account or another Workspace is refused (`hosted_domain_mismatch`). Link key is issuer plus `sub`. Google sends no group IDs; groups come from Google Workspace directory sync.
- **AD FS OIDC.** Issuer is the customer's own `https://host/adfs`, which may be reachable only inside their network (US5 scenario 3: discovery, keys and token calls go to that host only). The provider names the tenant's AD DS directory connection (`idp_config.directory_connection_id`, the Integrations connection the sync reads, checked when saved, so another tenant's connection is invisible). The `objectGUID` claim, as base64 of its 16 bytes (the AD FS default) or as text, links under that connection's issuer `urn:teambeats:onprem-ad:{connection}`, the same row directory sync created, so directory IDs from one connection never link identities from another. While sync is active JIT stays off (US6 scenario 3).
- **Why a token was refused.** `idp_config.last_problem` and `last_problem_at` keep the reason code of the provider's last refused token (such as `object_guid_missing`) and the admin page explains it (US5 scenario 2); the next good sign-in or a save clears it. The audit entry names the reason and the provider kind.
- **MFA.** The spec 026 claim mapping is keyed by provider kind (`entra`, `google`, `adfs`). Google sends no `amr`, so a tenant that requires MFA cannot use Google sign-in until its mapping can prove MFA; this is stated on the security page.
- **SAML 2.0 (slice 2).** `idp_config.protocol` is `oidc` or `saml`; SAML kinds are `generic` (any SAML 2.0 provider, including Google Workspace set up as a custom SAML app) and `adfs`. The `google` kind stays OIDC only, since the Google SAML NameID is the email address and changes on rename. The SAML provider keeps its entity ID in `issuer`, an https `sso_url` and one to three signing certificates (`saml_certificates`, RSA of at least 2048 bits, not expired, so a rollover can be prepared); it has no client ID or secret. SP-initiated with the HTTP-Redirect binding out and HTTP-POST back to `/api/v1/auth/signin/saml/acs`; SP metadata at `/api/v1/auth/signin/saml/metadata`, whose address is the SP entity ID. The request ID is stored single-use in `oidc_login_state` (empty code verifier marks SAML) with the same browser binding cookie (`tb_saml`, SameSite None, path the consumer address, because the IdP posts cross-site). The response is checked with `System.Security.Cryptography.Xml`, which ships in the ASP.NET Core shared framework the API already runs on, so no new package. Checks: DTD prohibited, status Success, `Destination`, `InResponseTo`, no encrypted assertion, exactly one assertion as a direct child, the response or the assertion signed by exactly one signature over its own unique ID with RSA-SHA256 or stronger, enveloped and exclusive canonicalization transforms only, verified with the configured certificates only (signature wrapping), issuer, conditions window with five minutes of clock skew, audience, bearer subject confirmation with recipient, `InResponseTo` and `NotOnOrAfter`. `generic` links on a persistent NameID under the entity ID (a transient or email NameID is refused as `name_id_not_persistent`); `adfs` links on the `objectGUID` attribute under the AD DS connection's issuer as above. Step-up sends `ForceAuthn` and checks `AuthnInstant`. The sign-in method is `saml_generic` or `saml_adfs`, and the MFA mapping reads `AuthnContextClassRef`. Entitlement `identity.saml` for the SAML protocol, `identity.oidc` for OIDC.
- **SCIM extension point (slice 3).** An `IProvisioningTarget` contract in Identity.Contracts (create, update, deactivate user, group membership) implemented by the directory sync use cases; no SCIM endpoint in R2 (FR-008, out of scope).
- Tests: per-kind validation, hosted domain refusal, AD FS link to the synced user with both claim forms, missing claim refused and shown to the admin, another tenant's connection refused, unknown AD FS user refused while sync decides, sign-in options by kind; SAML: success and link on persistent NameID, assertion-only signature, unsigned, foreign-key and tampered responses refused, signature wrapping refused, wrong audience, recipient, issuer, `InResponseTo`, expired and transient NameID refused, replay and another tenant's request refused, capability off, metadata, validation, AD FS over SAML linked on `objectGUID`.
