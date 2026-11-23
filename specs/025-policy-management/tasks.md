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

## R2: Reviews, checks, exemptions and evidence

R2 covers User Stories 5 to 8, FR-014 to FR-017, the AI change summary for approvers, paper attestation import and the manager dashboard. Routes run on the spec 016 and 024 engine and land in the task inbox; the control library is spec 026's, read through its contract.

- [x] T013 [US5] Scheduled review cycles: review cycle per policy (default 12 months) and window (default 60 days); a review task to the owner in the inbox with no change, minor update, major revision and retire; no change moves the next date and triggers no attestation; major revision starts a draft and the approval route, then asks the owner about re-attestation; Overdue for review in the register with notices to compliance and the owner's manager; tests including another tenant
- [x] T014 [US6] Comprehension checks: questions per policy version linked to clauses, pass mark and attempts per campaign; a failed attempt names the sections to reread, never the answers; after the last attempt "needs follow-up" and a notice to the owner; answers and scores kept with the attestation; keyboard and screen reader e2e
- [x] T015 [US7] Exemptions (FR-014): request with reason, scope and end date through the exemption route (owner, then compliance) in the inbox; the exemption register; the person's compliance view; reminders 14 days before expiry and automatic close; audit
- [ ] T016 [US8] Control and regulation mapping (FR-015): map policies to controls of the spec 026 control library; a control lists its policies and their status; tests that an unknown control is refused
- [ ] T017 [US8] Evidence packs (FR-016): per policy the versions in force, approval trail, review history, attestation completion by unit with the list, open exemptions and linked controls, as PDF (through the conversion service) and CSV, built in the background, audited, and shared read-only with a spec 026 auditor guest space until an expiry date
- [ ] T018 Onboarding attestations (FR-017): policies flagged "onboarding required" create attestation tasks through the spec 022 onboarding journey
- [ ] T019 Paper attestation import (section 14): CSV import with source "paper", the importer recorded and the scan attached, reported apart from online attestations; never a bulk "mark as attested"
- [ ] T020 AI change summary between versions for approvers through the AI Gateway, labelled as AI-generated, beside the compare view; manager dashboard of the team's attestation and exemption status with the minimum group size where analytics applies; long leave deadline pause with a reason
- [ ] T021 OpenAPI, R2 implementation notes, e2e with axe in `ar` and `en`, spec evidence
