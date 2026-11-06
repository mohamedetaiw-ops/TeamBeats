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

## Phase 2b: User Story 7, staging test account (P1, R0)
- [x] T029 [P] [US7] Tests: `admin` signs in with the configured password as tenant administrator; wrong password and unknown username refused and audited; email-only and platform dev sign-in refused in password mode; staging refuses open mode or a missing password; production host refuses test accounts and a test tenant; cross-tenant refusal; tenant seeded once
- [x] T030 [US7] Password mode in `Identity.DevAuth`, `Staging` environment in `Api.DevHost`, test tenant seeder through the tenancy use case, `appsettings.Staging.json` (no password), `deploy/docker/api-staging.Dockerfile`, staging sign-in form in the web app
- [x] T031 [US7] Production guard for test accounts and test tenant; `tools/deploy-lint.sh` in CI; architecture tests for passwords in configuration and production Dockerfiles

## Phase 3: User Story 3, session invalidation (P1, R0)
- [ ] T007 [P] [US3] Tests: sign-out ends session; disabled membership rejected on next request; role change effective within 5 minutes; suspended tenant sessions rejected
- [ ] T008 [US3] Revocation on `MembershipDisabled`, `TenantSuspended`; `roles_version` bump on `RoleAssignmentChanged`

## Phase 4: Tenant-scoped identity (R0)
- [ ] T009 [P] Test: same issuer and subject in tenants A and B creates two unrelated users (ADR-0005)
- [ ] T010 [P] Test: session cookie from tenant A rejected on tenant B host
- [ ] T011 IdP configuration admin API and screen (secret stored as secret-manager reference)

## Phase 5: User Story 2, Entra ID (P1, R1)
- [x] T012 [P] [US2] Tests: wrong issuer, wrong audience, expired, bad signature, missing nonce, replay
- [x] T013 [P] [US2] Tests: disabled membership shows no-access and audits; JIT allowed domain creates membership; JIT disabled refuses
- [x] T014 [P] [US2] Tests: group-to-role mapping applied; unknown groups ignored
- [ ] T015 [US2] Dynamic per-tenant OIDC handler; tenant discovery by host then email domain
- [x] T016 [US2] Sign-in, no-access, suspended and IdP-unavailable pages (localized, RTL)
- [x] T017 [US2] Open redirect and login CSRF protections with tests

## Phase 6: User Story 6, Entra ID directory sync (P1, R1; after approval of US6)
- [x] T020 Migration: `directory_connection`, `directory_user_link`, `directory_group_link`, owner fields on `user` (RLS)
- [x] T021 [P] [US6] Tests: test connection reports counts and stores nothing; write permission warning
- [x] T022 [P] [US6] Tests: first sync creates users keyed by `oid`; sign-in links with no duplicate even with JIT on; UPN change updates in place
- [x] T023 [P] [US6] Tests: disable in directory disables membership and revokes sessions; product admin cannot re-enable; re-enable restores the same user
- [x] T024 [P] [US6] Tests: groups kept in step and mapped to roles; product groups untouched; unreachable directory changes nothing and alerts after 3 failures; threshold breach stops the run
- [ ] T025 [US6] Graph delta reader, sync job on the outbox worker, `IDirectoryReader` contract for specs 002 and 010
- [ ] T026 [US6] Directory admin page (connections, scope, attribute and group mapping, last runs, sync now)

## Phase 7: R2 (not in R0/R1)
- [ ] T018 Google OIDC, SAML 2.0, SCIM extension point (IAM-005, IAM-006, IAM-010)
- [ ] T027 [US5] AD FS sign-in (OIDC and SAML) linked on `objectGUID`; missing claim refused with admin detail; works with no internet access on on-premises installs
- [ ] T028 [US6] On-premises AD DS sync over LDAPS through the connector framework and outbound sync agent; plain LDAP refused

## Final phase
- [ ] T019 Audit events for sign-in outcomes, config changes, directory connection changes and directory sync outcomes; OpenAPI; spec evidence
