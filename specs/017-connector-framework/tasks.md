# Tasks: Connector Framework and Integration Packs

Depends on 004, 005, 006, 015, 024, 026, 027. R2.

## Phase 1: Runtime
- [x] T001 Migration for packs, actions, webhooks, runs, dead letters, payload capture and sync agents (forced RLS)
- [x] T002 Permissions `integration.operate`; audit actions of section 13; capability keys of section 9
- [x] T003 [P] Tests: retry, backoff, idempotency, rate limit, circuit breaker, dead letter and replay; SSRF guard
- [x] T004 `IConnectorPack`, `IConnectorRuntime`, job runner, `SafeHttpClient`, run history
- [x] T005 Move the Microsoft 365 adapter onto the runtime with no behavior change (spec 018 and 006 suites stay green)

## Phase 2: User Stories 1 to 3, connections, actions and history (P1)
- [x] T006 [P] [US1] Tests: pack list by package; step-up and secret references; test per feature; disable stops calls within 1 minute; expiry alert
- [x] T007 [US1] Connection lifecycle on the runtime in the Connections area
- [x] T008 [P] [US2] Tests: mapped fields only; idempotent retry after timeout; business error branch; incident on unknown outcome; agent tool path
- [x] T009 [US2] Connector action steps for 016 and 024; tool registrations for 027
- [x] T010 [US3] Run history, dead letters and replay endpoints and pages

## Phase 3: User Stories 4 and 5, custom REST and webhooks (P2)
- [x] T011 [P] [US4] [US5] Tests: custom action test with sample data; allowlist and private address refusal; undeclared field refusal; signed outbound; inbound signature, window and duplicate; secret rotation overlap
- [x] T012 [US4] Custom REST connector and builder page
- [x] T013 [US5] Outbound and inbound webhooks

## Phase 4: User Story 6, on-premises agent (P1)
- [x] T014 [P] [US6] Tests: enrolment, certificate binding, revocation within 1 minute, one taker per connection, offline status
- [x] T015 [US6] Sync agent service and endpoint; AD pack over LDAPS feeding specs 002 and 003

## Phase 5: User Stories 7 and 8, packs (P2, P3)
- [x] T016 [US7] SFTP and CSV clock data import; finance hand-off contract
- [x] T017 ServiceNow and Jira Service Management ticketing packs
- [x] T018 [US8] Slack pack for notifications and sharing
- [x] T019 Google Workspace directory and Drive extension points

- [x] T021 KPI feed contract (FR-022): signed inbound webhooks with the target `kpi`, one reading per delivery, kept 400 days

## Final phase
- [x] T020 Connector-down suite in CI; performance tests; OpenAPI; R2 implementation notes; spec evidence
