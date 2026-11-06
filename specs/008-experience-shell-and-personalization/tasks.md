# Tasks: Experience Shell, Navigation, Employee Home and Personalization

Depends on 032 (`Experience` module, pages, renderer), 033 (tokens), 003, 004, 005, 006, 007. R1 covers User Stories 1, 3 and 5, User Story 2 add, hide, reorder and reset, User Story 4 without favorites, recents and commands, and the policy and consent model of User Story 6.

## Phase 1: Shared states and interaction primitives (P1, US1, US5)
- [x] T001 `components/ui` state views (loading, success, empty, error, permission denied, offline, unsupported) and message rules (critical persists, low priority never blocks)
- [x] T002 Button hierarchy, confirm and undo, form field errors with announcements, skeletons, density presets, reduced motion
- [x] T003 [P] [US5] Tests: one primary action per context, destructive look, errors linked and announced with values kept, both densities at 200% zoom, reduced motion stops nonessential animation

## Phase 2: Shell and navigation (P1, US1, US4)
- [x] T004 Migration: `shell_config`, `nav_menu`, `ia_node`, `slug_redirect` with forced RLS; permissions `shell.edit`, `navigation.edit`, `help.edit`
- [x] T005 [P] [US1] [US4] Tests: shell publish changes no page; each menu publishes alone; menus and quick navigation list only permitted pages; same recovery answer for missing, unpublished and not permitted; old slugs redirect; breadcrumbs follow the IA; sibling org admin cannot change another org's menu
- [x] T006 [US1] [US4] Shell, menus, IA, resolve, breadcrumbs and quick navigation endpoints; audit and `ShellPublished`, `NavigationPublished`

## Phase 3: Preferences and personal layouts (P1, US2, US3)
- [ ] T007 Migration: `personalization_policy`, `preference_profile`, `personal_layout`, `audience_rule` with forced RLS; permissions `personalization.policy`, `personal_layout.edit`; capability keys of section 9; default home policy for new tenants
- [ ] T008 [P] [US2] Tests: add only in delegated regions with eligible components; protected placement hide or remove refused through the API (S04); coworkers unaffected (S03); region maximum; reset one and reset all keep other preferences; skipped operations reported, not deleted; another user's layout not found
- [ ] T009 [P] [US3] Tests: default variant when no audience matches; hidden placement does not change the component API's answer; precedence collisions for user choice, admin default, audience rule, parent lock and legal constraint
- [ ] T010 [US2] [US3] Preference, layout, add panel, policy, audience rule and targeting preview endpoints

## Phase 4: Shell and home frontend (P1)
- [ ] T011 [US1] [US4] Shell frame, accessible menus, mobile navigation, organization indicator, breadcrumbs and settings panel in English and Arabic, RTL
- [ ] T012 [US2] Personalization mode with add panel, move actions without drag, live announcements, undo and reset with confirmation
- [ ] T013 Default home template with protected and personal regions; My mail and My calendar in the personal region when spec 018 features are on (FR-051)
- [ ] T014 Tool box utilities (weather, world clock, time zone converter) as platform components with manifests
- [ ] T015 [P] Tests: menus by keyboard and screen reader, personalization by keyboard only, mobile reachability, axe in `ar` and `en`

## Final phase
- [ ] T016 OpenAPI, R1 implementation notes, spec evidence
