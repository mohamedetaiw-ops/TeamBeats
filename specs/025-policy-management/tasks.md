# Tasks: Policy Management

Depends on 004, 005, 006, 007, 009 (body document, audience model), 011 (controlled documents), 016 (task inbox source), 024 (sequential steps); 010 (reporting line), 012 (search), 014 (assistant), 015 (notifications). R1 covers User Stories 1 to 4.

## Phase 1: Foundations
- [x] T001 `Policies` and `Policies.Contracts` projects; migration for policies, versions, clause comments, campaigns, members and attestations (forced RLS; attestations insert only for the app role)
- [x] T002 Permissions `policy.author`, `policy.manage`; audit actions of section 13 for R1; capability keys `policy.core`, `policy.campaigns`, `ai.policy_qa`

## Phase 2: User Story 1, publish through approval (P1)
- [x] T003 [P] [US1] Tests: sequential route with compare; clause comments; numbering, effective date and superseded on the date; binding language note and joint approval of variants; superseded banner; no self-approval
- [x] T004 [US1] Register, versions with clauses, route and publishing

## Phase 3: User Story 2, attestation campaigns (P1)
- [x] T005 [P] [US2] Tests: launch creates tasks and My policies entries; immutable record with hash; reminders and overdue; late joiner and leaver; material revision choice; excluded membership type; one open campaign on `policy.core`
- [x] T006 [US2] Campaigns, members, attestation, scheduler and task source

## Phase 4: User Story 3, compliance view (P1)
- [x] T007 [P] [US3] Tests: completion by unit with drill-down; per-person view; scoped compliance officer
- [x] T008 [US3] Compliance endpoints; manager breakdown once the reporting line exists

## Phase 5: User Story 4, policy Q&A (P1)
- [x] T009 [US4] Clause units published for search with stable anchors; superseded excluded by default
- [x] T010 [US4] Assistant answers with clause citations and the policy-scoped entry, with the assistant spec; tests for scenarios 1 to 6

## Phase 6: Experience
- [x] T011 Register, editor with clauses, policy page with anchors, My policies, campaign and compliance pages, in English and Arabic, RTL; e2e with axe

## Final phase
- [x] T012 OpenAPI; R1 implementation notes (location and manager breakdowns); spec evidence
