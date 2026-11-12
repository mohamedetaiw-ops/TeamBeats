# Tasks: Communities and Engagement

Depends on 002, 004, 009, 010, 011, 012, 015, 026. R2.

## Phase 1: Module setup
- [x] T001 `Engagement` module skeleton per ADR-0013; migration for section 8 entities (forced RLS)
- [x] T002 Permissions, relations, capability keys and audit actions of sections 3, 9 and 13

## Phase 2: User Stories 1 and 8, communities and moderation (P1)
- [x] T003 [P] [US1] Tests: community types, closed invisibility, department membership, mentions, grouped notifications, search access
- [x] T004 [US1] Communities, channels, posts, comments, reactions, hashtags, following
- [x] T005 [P] [US8] Tests: reports and auto hide, keyword hold, removal reason and search removal, mutes
- [x] T006 [US8] Moderator queue and actions

## Phase 3: User Stories 2 and 3, Speak Up, polls and surveys (P1)
- [x] T007 [P] [US2] [US3] Tests: no identity stored for anonymous items; daily hash; review queue; one vote; threshold hiding everywhere; responded marker deleted at close
- [x] T008 [US2] Speak Up channel and review queue
- [x] T009 [US3] Polls, survey builder, results and export

## Phase 4: User Stories 4 to 7 (P1 to P3)
- [x] T010 [P] [US4] [US5] Tests: recognition, no self recognition, caps, points off hides all
- [x] T011 [US4] Recognition, badges, values, points and leaderboard
- [x] T012 [US5] Period awards
- [x] T013 [US6] Celebrations and widget (the home widget ships with the other engagement widgets in T015)
- [ ] T014 [US7] Galleries with scanning

## Final phase
- [ ] T015 Home widgets for 008; performance tests; OpenAPI; R2 implementation notes; spec evidence
