# Tasks: Security Controls, Compliance and Certifications

Depends on 001 (break-glass), 003 (sessions, Entra sign-in), 004 (conditions, explainer), 005 (entitlements), 006 (audit, admin shell), 022 (tenant working week for out-of-hours alerts). R1 covers User Stories 1, 2 and 5 (basic), FR-017 and FR-025. R2 covers User Stories 3 (with spec 029), 4, 5 (full), 6 and 7, FR-008 and FR-020. R3 covers User Story 8 and FR-016.

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
- [x] T016 Admin pages: Security policy (with lock-out preview), Alerts, Posture, in English and Arabic, RTL; e2e with axe

## Final phase
- [x] T017 OpenAPI; R1 implementation notes; spec evidence

## R2 Phase 6: User Story 4, SIEM streaming (P2)
- [x] T018 [P] [US4] Tests: HTTPS JSON signed and in order; syslog over TLS; resend from the cursor after an outage with no gaps; test event; secret rotation with step-up; cross-tenant; package; configuration audit
- [x] T019 [US4] `IAuditStream` in `Audit.Contracts`; `security.siem_destination`; the unlisted `siem` pack with HTTPS and syslog delivery through Integrations; the delivery worker
- [x] T020 [US4] SIEM endpoints and admin page; `docs/security/siem-schema.md`

## R2 Phase 7: User Story 3, guests (P1)
- [x] T021 [P] [US3] Tests: guest domain rules; `security.max_guest_spaces`; reduced shell; watermark; access reviews for guests and admin role holders
- [x] T022 [US3] Guest policy and limit checked by Spaces; reduced guest shell; watermarked PDF downloads
- [x] T023 [US3] Access reviews (FR-012) with automatic removal after 14 days

## R2 Phase 8: User Story 5 in full (P1)
- [ ] T024 [P] [US5] Tests: guest opens a Confidential item; app denial burst; app volume spike; thresholds
- [ ] T025 [US5] The rules, `ApiCallsDenied` and `ApiVolumeSpike` from PublicApi, threshold settings

## R2 Phase 9: User Story 6, sensitive data detection (P2)
- [ ] T026 [P] [US6] Tests: built-in patterns; warn and confirm; block by location type; tenant pattern test; nothing matched is logged
- [ ] T027 [US6] `ISensitiveDataScanner`, patterns, settings; hooks in Content and Documents; warning UI

## R2 Phase 10: User Story 7, compliance projects (P2)
- [ ] T028 [P] [US7] Tests: project from a framework; evidence with export hash; readiness; guest auditor read-only; org scope; package
- [ ] T029 [US7] `Compliance` module, control library, projects, controls, evidence tasks, readiness view, sharing to guest spaces; pages

## R2 Phase 11: residency and platform access
- [ ] T030 Region at tenant creation and on the posture page (FR-008); break-glass entries in the tenant's audit view (FR-020)

## R2 final phase
- [ ] T031 OpenAPI; e2e in English and Arabic with axe; R2 implementation notes; spec evidence
