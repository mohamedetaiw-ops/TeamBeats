# Tasks: Security Controls, Compliance and Certifications

Depends on 001 (break-glass), 003 (sessions, Entra sign-in), 004 (conditions, explainer), 005 (entitlements), 006 (audit, admin shell), 022 (tenant working week for out-of-hours alerts). R1 covers User Stories 1, 2 and 5 (basic), FR-017 and FR-025; the rest is R2 and R3.

## Phase 1: Foundations
- [x] T001 `Security` and `Security.Contracts` projects; migration for `security_policy`, `mfa_claim_mapping`, `security_alert` (forced RLS); `user_session.last_seen_at` and `authenticated_at` in `Identity`
- [x] T002 Permissions `security.manage`, `security.view`, `security.respond`; security officer role grants; capability keys of section 9; audit actions `security.policy_changed`, `security.request_blocked_network`, `security.alert_raised`, `security.alert_handled`, `identity.step_up_completed`
- [x] T003 `ISecurityPolicy` contract with a per-tenant cache invalidated by `SecurityPolicyChanged`

## Phase 2: User Story 1, restrict access by network (P1)
- [x] T004 [P] [US1] Tests: tenant allow and block lists; admin area list; forwarded header spoofing ignored; self lock-out needs step-up and confirmation; network zone condition and explainer; Workplace refuses scoped rules; cross-tenant
- [x] T005 [US1] Network middleware with `AdminArea` endpoint metadata and rate-limited refusal audit
- [x] T006 [US1] `network_zone` condition attribute in spec 004 conditions
- [x] T007 [US1] Policy endpoints with preview count of affected users

## Phase 3: User Story 2, MFA and step-up (P1)
- [x] T008 [P] [US2] Tests: MFA claim mapping for Entra; refusal without MFA; step-up required and satisfied with a fresh `auth_time`; idle and absolute timeouts with the fake clock; concurrent limit; revoke all within 1 minute
- [x] T009 [US2] MFA check in the Entra sign-in callback; `require_mfa` turns on only after a test sign-in shows the claim
- [x] T010 [US2] `RequireStepUp` metadata on the section 6 endpoints that exist; step-up redirect with `prompt=login` and `max_age=0`; web shell handling of `security.step_up_required`
- [x] T011 [US2] Session idle, absolute and concurrent rules in the session middleware; revoke-all endpoints

## Phase 4: User Story 5, security alerts (P1, basic)
- [x] T012 [P] [US5] Tests: download volume threshold from `DocumentDownloaded`; out-of-hours admin grant (advanced key); handling audited; cross-tenant
- [x] T013 [US5] Alert rules, list and handle endpoints; admin shell badge

## Phase 5: Posture, secure rendering and pages
- [x] T014 Posture endpoint and checks
- [x] T015 Test that the content security policy has no inline script and that rich text goes through the sanitizer (FR-025)
- [ ] T016 Admin pages: Security policy (with lock-out preview), Alerts, Posture, in English and Arabic, RTL; e2e with axe

## Final phase
- [ ] T017 OpenAPI; R1 implementation notes; spec evidence
