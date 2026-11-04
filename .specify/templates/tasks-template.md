# Tasks: [FEATURE NAME]

**Input:** `specs/[NNN-name]/spec.md`, `plan.md`
**Format:** `[ID] [P?] [Story] Description (file path)`. `[P]` means it can run in parallel with other `[P]` tasks (different files, no dependency).

Tests are written first and must fail before the implementation task that makes them pass.

## Phase 1: Setup
- [ ] T001 ...

## Phase 2: Foundations (blocks all stories)
- [ ] T0xx Migration and RLS policy for ...
- [ ] T0xx Cross-tenant negative test harness for ...

## Phase 3: User Story 1 (P1)
### Tests first
- [ ] T0xx [P] [US1] Acceptance test: ...
- [ ] T0xx [P] [US1] Cross-tenant denial test: ...
### Implementation
- [ ] T0xx [US1] ...

## Phase 4: User Story 2 (P2)
...

## Final phase: Polish and reconciliation
- [ ] Audit events asserted
- [ ] Telemetry and correlation IDs verified
- [ ] OpenAPI updated and diff-checked
- [ ] Localization keys added for all five locales, RTL checked
- [ ] Spec status updated with implementation evidence
- [ ] Constitution review recorded in the PR
