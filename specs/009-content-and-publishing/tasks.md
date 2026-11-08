# Tasks: Content, Publishing and Governance

Depends on 003 (groups, `UserDeprovisioned`), 004, 005, 006, 007, 008 (shell slots, shared states), 016 (task inbox), 024 (engine core), 032 (component manifests), 033 (tokens); 010 (reporting line, locations), 012 (search), 015 (notifications). R1 covers User Stories 1 to 5 (manual translation), 7 and 9 (reactions and comments).

## Phase 1: Foundations
- [x] T001 `Content` and `Content.Contracts` projects; migration for `site`, `content_type`, `content_item`, `content_version`, `acknowledgement`, `audience_member`, `comment`, `reaction` (forced RLS)
- [x] T002 Permissions `content.author`, `content.publish_critical`, `content.admin` alongside the existing keys; audit actions of section 13; capability keys of section 9
- [x] T003 Body document schema and validator with unit tests (allowed blocks and marks, embed allowlist, size limits)
- [x] T004 Audience model and evaluator (org unit with or without subtree, group, role, membership type) with unit tests

## Phase 2: User Story 1, targeted news with approval (P1)
- [x] T005 [P] [US1] Tests: submit creates an inbox task; return with comment; scheduled and late approval; in-audience feed and sibling exclusion; edit of a published item keeps it live; no self-approval
- [x] T006 [US1] Items, versions and lifecycle endpoints; scheduler for publish and expiry
- [x] T007 [US1] One-step route on the spec 024 engine core with the inbox task
- [x] T008 [US1] Feed endpoint with permission filtering and cursor paging

## Phase 3: User Story 2, critical announcements (P1)
- [x] T009 [P] [US2] Tests: modal until confirmed; remind later limit; record with text hash; material change resets; `content.publish_critical` required; banner; background report audited
- [x] T010 [US2] Pending check, acknowledge and remind later; audience membership table and refresh on membership change
- [x] T011 [US2] Deadline reminders through notifications; manager list once the reporting line exists; completion report export

## Phase 4: User Stories 3 to 5, governance, versions and locales (P1)
- [ ] T012 [P] [US3] Tests: owner required; review task with confirm, edit, retire; overdue label; ownerless reassignment on `UserDeprovisioned`; health counts
- [ ] T013 [US3] Review scheduler, review actions and health endpoint
- [ ] T014 [P] [US4] Tests: history, compare with text markers, restore creates a draft, readers see no history
- [ ] T015 [US4] Versions, compare and restore endpoints
- [x] T016 [P] [US5] Tests: variant per locale, fallback note, out-of-date flag
- [x] T017 [US5] Locale variants in versions and fallback on read

## Phase 5: User Stories 7 and 9, components and social (P2)
- [ ] T018 [P] [US7] Tests: components in every compatible region and width in both directions; viewer permissions on every render; embed allowlist; refused without capability
- [ ] T019 [US7] Manifests and React components: news feed, rich text, links, call to action, announcement banner
- [ ] T020 [P] [US9] Tests: comment notifies the owner; moderation holds comments; reports hide a comment; cross-tenant IDs
- [ ] T021 [US9] Comments and reactions endpoints and UI

## Phase 6: Authoring experience
- [ ] T022 Block editor on Lexical with the body document format, RTL and mixed direction, keyboard and screen reader support
- [ ] T023 Item editor pages: audience picker, schedule and expiry, owner and review date, critical options, preview per audience and device; history and compare views; in English and Arabic; e2e with axe

## Final phase
- [ ] T024 Performance tests of section 15; OpenAPI; R1 implementation notes (sites and targeting defaults); spec evidence
