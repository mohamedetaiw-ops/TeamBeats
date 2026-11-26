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
- [x] T014 [US7] Galleries with scanning (resizing is a follow-up, see the R2 notes)

## Final phase
- [x] T015 Home widgets for 008; performance tests; OpenAPI; R2 implementation notes; spec evidence

## R3

Depends on 015 (Teams bot), 017 (Slack), 013 (AI Gateway), 021 (sentiment). R3 covers User Stories 9 to 11 and FR-027 to FR-033.

### Phase 5: User Story 9, discussion rooms (P2)
- [ ] T016 [US9] Rooms and messages: migration, capability `engagement.rooms`, audit actions, API, live stream, mentions, keyword hold, reports and removal in the moderator queue; integration tests
- [ ] T017 [US9] Rooms on the community page and the room page with live updates; e2e

### Phase 6: User Story 10, mirroring (P3)
- [ ] T018 [US10] Mirrors to Slack and Teams channels: migration, capability `engagement.mirroring`, Teams channel post through the bot, `PostPublished` handler, section on the community settings; tests

### Phase 7: User Story 11, AI summaries and sentiment trends (P3)
- [ ] T019 [US11] Thread and room summaries through the AI Gateway, tenant setting for anonymous content; tests
- [ ] T020 [US11] Sentiment topic key and trends contract in 021, room and Speak Up sources, trend on the community page; tests

### Final phase (R3)
- [ ] T021 OpenAPI; R3 implementation notes; spec evidence
