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
- [x] T012 [P] [US3] Tests: owner required; review task with confirm, edit, retire; overdue label; ownerless reassignment on `UserDeprovisioned`; health counts
- [x] T013 [US3] Review scheduler, review actions and health endpoint
- [x] T014 [P] [US4] Tests: history, compare with text markers, restore creates a draft, readers see no history
- [x] T015 [US4] Versions, compare and restore endpoints
- [x] T016 [P] [US5] Tests: variant per locale, fallback note, out-of-date flag
- [x] T017 [US5] Locale variants in versions and fallback on read

## Phase 5: User Stories 7 and 9, components and social (P2)
- [x] T018 [P] [US7] Tests: components in every compatible region and width in both directions; viewer permissions on every render; embed allowlist; refused without capability
- [x] T019 [US7] Manifests and React components: news feed, rich text, links, call to action, announcement banner
- [x] T020 [P] [US9] Tests: comment notifies the owner; moderation holds comments; reports hide a comment; cross-tenant IDs
- [x] T021 [US9] Comments and reactions endpoints and UI

## Phase 6: Authoring experience
- [x] T022 Block editor on Lexical with the body document format, RTL and mixed direction, keyboard and screen reader support
- [x] T023 Item editor pages: audience picker, schedule and expiry, owner and review date, critical options, preview per audience and device; history and compare views; in English and Arabic; e2e with axe

## Final phase
- [x] T024 Performance tests of section 15; OpenAPI; R1 implementation notes (sites and targeting defaults); spec evidence

## R2

### Phase 7: User Story 6, sites (P2)
- [x] T025 [P] [US6] Tests: create from a template links home page, navigation and theme scope; sibling site owner denied; reader site list follows default and shared audience; archived site read-only, out of feeds and navigation; site limit; cross-tenant site IDs
- [x] T026 [US6] Site templates, home page link, status and archive, shared audience, `GET /sites/mine`; site admin pages and site home route in the web app, English and Arabic, e2e with axe

### Phase 8: User Story 9 R2 and FR-017, mentions, hashtags and follows (P2)
- [x] T027 [P] [US9] Tests: mention notifies only a permitted reader; suggestions follow directory permission; topic page lists only permitted items; follow and the following filter; critical items never hidden
- [x] T028 [US9] Mention marks and hashtags in bodies and comments, topic endpoint and page, follows and feed filter, UI

### Phase 9: User Story 5 R2, AI translation (P2)
- [ ] T029 [P] [US5] Tests: translation draft marked machine translated; Confidential blocked by default policy; reviewer required before publish; human edit clears the mark; entitlement off refused; gateway unavailable degrades
- [ ] T030 [US5] Translate endpoint through the AI Gateway, variant mark, content type `reviewed_translation`, editor actions and badges

### Phase 10: multi-step approval (FR-003)
- [ ] T031 [P] Tests: steps run in order with a task each; return at any step; author excluded at every step; more than one step refused without `content.multistep_approval`
- [ ] T032 Route steps in site routes and the engine definition; route editor in the site settings

### Phase 11: User Story 8, campaigns (P2)
- [ ] T033 [P] [US8] Tests: due steps sent to the audience through notifications; "only those who have not acted" excludes completers; optional email respects preferences, mandatory labelled; dashboard counts with privacy threshold; entitlement and cross-tenant cases
- [ ] T034 [US8] Campaign model, scheduler, sends through spec 015, dashboard endpoint and pages, English and Arabic, e2e with axe

### Phase 12: retention, health dashboard and signage
- [ ] T035 [P] Tests: retention deletes old versions after the period and skips legal hold; health dashboard counts ownerless sites and overdue items per site with permitted items only; signage URL shows only public items of the site and stops after rotation
- [ ] T036 Retention settings and job, health dashboard endpoint and page, signage screens and feed

### R2 final phase
- [ ] T037 OpenAPI; R2 implementation notes; spec evidence for R2
