# Tasks: Identity and Authentication

Depends on 001 Phase 1 and ADR-0007. R0 tasks first, R1 tasks marked.

## Phase 1: Foundations (R0)
- [ ] T001 Migration: `user`, `external_identity`, `idp_config`, `session` (RLS); `verified_domain` (platform, RLS exception)
- [ ] T002 Server-side session store, opaque cookie (secure, HTTP-only, SameSite=Lax), session validation middleware with 30 s cache and event eviction

## Phase 2: User Story 1, dev sign-in (P1, R0)
- [ ] T003 [P] [US1] Test: production configuration with dev sign-in enabled fails startup
- [ ] T004 [P] [US1] CI script and test: production image must not contain `Identity.DevAuth.dll` or `/dev-signin`
- [ ] T005 [US1] `Identity.DevAuth` assembly with seeded fictional users; referenced only by `Api.DevHost`
- [ ] T006 [US1] Startup guard in `Api`

## Phase 3: User Story 3, session invalidation (P1, R0)
- [ ] T007 [P] [US3] Tests: sign-out ends session; disabled membership rejected on next request; role change effective within 5 minutes; suspended tenant sessions rejected
- [ ] T008 [US3] Revocation on `MembershipDisabled`, `TenantSuspended`; `roles_version` bump on `RoleAssignmentChanged`

## Phase 4: Tenant-scoped identity (R0)
- [ ] T009 [P] Test: same issuer and subject in tenants A and B creates two unrelated users (ADR-0005)
- [ ] T010 [P] Test: session cookie from tenant A rejected on tenant B host
- [ ] T011 IdP configuration admin API and screen (secret stored as secret-manager reference)

## Phase 5: User Story 2, Entra ID (P1, R1)
- [ ] T012 [P] [US2] Tests: wrong issuer, wrong audience, expired, bad signature, missing nonce, replay
- [ ] T013 [P] [US2] Tests: disabled membership shows no-access and audits; JIT allowed domain creates membership; JIT disabled refuses
- [ ] T014 [P] [US2] Tests: group-to-role mapping applied; unknown groups ignored
- [ ] T015 [US2] Dynamic per-tenant OIDC handler; tenant discovery by host then email domain
- [ ] T016 [US2] Sign-in, no-access, suspended and IdP-unavailable pages (localized, RTL)
- [ ] T017 [US2] Open redirect and login CSRF protections with tests

## Phase 6: R2 (not in R0/R1)
- [ ] T018 Google OIDC, SAML 2.0, SCIM extension point (IAM-005, IAM-006, IAM-010)

## Final phase
- [ ] T019 Audit events for sign-in outcomes and config changes; OpenAPI; spec evidence
