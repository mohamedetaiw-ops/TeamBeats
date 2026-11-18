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
- [x] T007 Migration: `personalization_policy`, `preference_profile`, `personal_layout`, `audience_rule` with forced RLS; permissions `personalization.policy`, `personal_layout.edit`; capability keys of section 9; default home policy for new tenants
- [x] T008 [P] [US2] Tests: add only in delegated regions with eligible components; protected placement hide or remove refused through the API (S04); coworkers unaffected (S03); region maximum; reset one and reset all keep other preferences; skipped operations reported, not deleted; another user's layout not found
- [x] T009 [P] [US3] Tests: default variant when no audience matches; hidden placement does not change the component API's answer; precedence collisions for user choice, admin default, audience rule, parent lock and legal constraint
- [x] T010 [US2] [US3] Preference, layout, add panel, policy, audience rule and targeting preview endpoints

## Phase 4: Shell and home frontend (P1)
- [x] T011 [US1] [US4] Shell frame, accessible menus, mobile navigation, organization indicator, breadcrumbs and settings panel in English and Arabic, RTL
- [x] T012 [US2] Personalization mode with add panel, move actions without drag, live announcements, undo and reset with confirmation
- [x] T013 Default home template with protected and personal regions; My mail and My calendar in the personal region when spec 018 features are on (FR-051)
- [x] T014 Tool box utilities (weather, world clock, time zone converter) as platform components with manifests
- [x] T015 [P] Tests: menus by keyboard and screen reader, personalization by keyboard only, mobile reachability, axe in `ar` and `en`
- [x] T017 [US1] [US3] Footer (FR-056) and home view tabs (FR-058) in the shell configuration, validated, resolved per viewer and audience, a shell settings page in administration and the staging seed; integration tests for validation, audience, capability and another tenant, browser tests in `ar` and `en` with axe

## Final phase
- [x] T016 OpenAPI, R1 implementation notes, spec evidence
- [x] T018 [US1] Administration row in the account menu from the shell API's `administration` flag (FR-062); tests for admin, scoped admin, security officer, employee, contractor and another tenant, and the account menu path in `ar` and `en`

## R2: Find, shape and learn the intranet

R2 covers User Story 2 resize and presets, User Story 4 favorites, recents and commands, and FR-009, FR-020, FR-032, FR-034 and FR-035. Every list the shell shows is resolved by the API for the viewer; the web app only draws what it is given.

- [x] T019 [US4] Favorites and recents (FR-017, US4 scenario 6): migration for `favorite` and `recent_visit` (forced RLS, per user, at most 50 favorites and the last 30 recents); a favorite is a page, IA node or permitted item link resolved again on every read, so one whose access is revoked shows disabled with a reason and is never followed; clear recents; recents keep no query strings; endpoints, the shell's favorites menu and recents list in the quick navigation box; tests for revocation, another user and another tenant
- [ ] T020 [US4] Command entry point (FR-008, US4 scenario 8): the Ctrl+K palette lists permitted commands as well as pages; modules register commands through an Experience contract with a capability key and a permission, and the API lists only those the viewer holds; first commands: new request, ask the assistant, book a room, request leave, open my tasks; tests that a command without the permission or the capability never appears
- [ ] T021 [US2] Resize within constraints and personal presets (FR-025 R2, FR-026): placement sizes from the component manifest's allowed widths; resize by pointer and by keyboard; presets default, compact and focus applied in one action and reset, with no change to entitlements; tests for a size the manifest does not allow and a protected placement
- [ ] T022 Device-specific choices and preference export (FR-032, FR-034, FR-036): each preference classified as cross-device or device-specific in the profile schema; a device class (desktop, tablet, phone) on personal layouts that never makes another device's order invalid; export of the person's own preferences as versioned JSON with no other user's or organization's data, and reset; tests
- [ ] T023 Focus or reading mode (FR-009): a shell toggle that hides the rails and secondary regions and keeps headings, landmarks and the skip link; remembered per device; axe in both modes
- [ ] T024 In-context help and tours (FR-020): help hints and short tours as versioned, schema-validated tenant configuration (`help.edit`), dismissible and reopenable from the help menu, never a blocking overlay; dismissals stored per user; tests and e2e in `ar` and `en`
- [ ] T025 Safe suggestions (FR-035): rule-based suggestions (for example add a component the person's unit uses, pin a page visited often) that say why they are offered, need acceptance and can be undone; nothing changes a stored preference without acceptance; off when the tenant's personalization policy turns it off
- [ ] T026 OpenAPI, R2 implementation notes, e2e with axe for favorites, commands, presets, focus mode and tours in `ar` and `en`
