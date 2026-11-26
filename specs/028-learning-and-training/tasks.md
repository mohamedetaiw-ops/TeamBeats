# Tasks: Learning and Mandatory Training

Depends on 002, 009, 011, 015, 017, 022, 025. R2.

## Phase 1: Module setup
- [x] T001 `Learning` module skeleton; migration for section 8 entities (forced RLS)
- [x] T002 Permissions, capability keys and audit actions

## Phase 2: User Stories 1 and 3, courses and learning (P1)
- [x] T003 [P] [US1] [US3] Tests: referenced lessons, versioning, quiz pass and attempts, language versions, resume, catalogue targeting
- [x] T004 [US1] Course builder, quizzes and paths
- [x] T005 [US3] Course player, My learning page and widget, certificates

## Phase 3: User Stories 2, 4 and 5, assignments and compliance (P1)
- [x] T006 [P] [US2] [US4] Tests: unit assignment, new members, leavers, recurrence, reminders and escalation, duplicates, scope limit, onboarding and policy triggers, retraining
- [x] T007 [US2] Assignment rules and job
- [x] T008 [US4] Consumers for 022 and 025
- [x] T009 [US5] Reports and evidence export

## Phase 4: User Stories 6 and 7 (P2)
- [x] T010 [US6] LMS connector contract and generic pack
- [x] T011 [US7] SCORM 1.2 and xAPI import and sandboxed player

## Final phase
- [x] T012 Performance test with 20,000 learners; OpenAPI; R2 implementation notes; spec evidence

## R3

Depends on 010 (skills), 013 (AI Gateway), 015. R3 covers User Stories 8 to 10 and LRN-021 to LRN-030.

### Phase 5: User Story 8, quiz drafts (P2)
- [ ] T013 [US8] Quiz drafts through the AI Gateway, AI draft mark, publish refused until reviewed, audit; builder controls; tests

### Phase 6: User Story 9, skill recommendations (P3)
- [ ] T014 [US9] `IPersonSkills` contract, course skill tags, recommendations and dismissals on My learning; tests

### Phase 7: User Story 10, classroom sessions (P2)
- [ ] T015 [US10] Classroom lesson kind, capability `learning.classroom`, sessions, seats, waiting list, reminders, attendance completing the lesson; integration tests
- [ ] T016 [US10] Sessions in the builder, seat booking in the player, facilitator roster page; e2e

### Final phase (R3)
- [ ] T017 OpenAPI; R3 implementation notes; spec evidence
