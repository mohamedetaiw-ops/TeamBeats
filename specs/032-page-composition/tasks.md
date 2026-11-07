# Tasks: Page Composition

Depends on 002 (org units), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization). R1 covers User Stories 1 to 5, 7 and 8, and publish and rollback of User Story 6. Approval routes, impact reports, fragments, scheduling, import and export are R2.

## Phase 1: Foundations
- [x] T001 `Experience` and `Experience.Contracts` projects, module registration, migration `0001_experience.sql` (`component_enablement`, `page`, `page_draft`, `page_release`) with forced RLS on tenant tables
- [x] T002 Permissions `page.view`, `page.edit`, `page.publish`, `template.edit`, `template.distribute`, `component.enable`, `config_history.view`; built-in roles `page_editor` and `page_publisher`; capability keys of section 9 in every package
- [x] T003 Platform catalogue: layouts L01 to L10 and templates T01 to T12 as embedded JSON, with variants spacious, balanced and compact
- [x] T004 Component manifest model, restricted property schema validator, manifest validation at registration, `IComponentRegistration` in contracts
- [x] T005 Reference packs `reference.alpha` and `reference.beta` (manifests in the dev host; their web components come with T015)

## Phase 2: User Story 1 and 3, compose valid pages (P1)
- [x] T006 [P] [US1] [US3] Tests: page from a template records template and layout provenance; picker lists only registered, enabled, entitled, permitted components compatible with the region, with reasons for the rest; region policy refusals name the rule; invalid spans, singletons, unknown components, executable content and missing regions are blocked with the placement ID; draft never visible to employees (overlap, nesting and breakpoint order checks come with sections and order in T016)
- [x] T007 [US1] [US3] Validator, page create, draft save with `If-Match`, validate, picker and catalogue endpoints

## Phase 3: User Story 6, publish and rollback (P1, R1 part)
- [x] T008 [P] [US6] Tests: publish creates an immutable release; rollback restores an earlier release as a new release; delegated editor outside scope gets 404 and the attempt is audited; stale `If-Match` gives 409 with both versions
- [x] T009 [US6] Publish, rollback and releases endpoints; audit and `PagePublished`, `PageRolledBack`

## Phase 4: User Story 5, templates (P1)
- [x] T010 [P] [US5] Tests: each of the 12 templates instantiates with no placements; variants change presentation only; template version 2 does not change existing pages; a template distributed to one org is invisible and not usable in another; protected regions cannot be removed through the API; deprecated version keeps pages working and blocks new ones; layout change lists placements with no compatible region
- [x] T011 [US5] Tenant template copies with locks and distribution, `experience.max_custom_templates` limit, layout change endpoint

## Phase 5: User Story 7 and 8, registry and isolation (P1)
- [x] T012 [P] [US7] [US8] Tests: invalid manifest not registered; out-of-range upgrade blocked; migration hook converts props; undeclared context keys are not supplied; executable content in any field refused; page and component config from another tenant not found
- [x] T013 [US7] Component enablement endpoint, render endpoint with effective structure, release snapshots cached by tenant, page and release (immutable, so no invalidation)
- [x] T014 Architecture test: no template, layout or page file names a concrete component ID (the import rule between placement packages comes with T015)

## Phase 6: Renderer (with spec 008 shared states)
- [x] T015 `lib/experience`: render client, registry with lazy placements, placement decorator (error boundary, placeholder, visibility, telemetry), event channel with declared events only, context provider
- [x] T016 The 10 layouts with CSS grid, container queries, logical properties and fallbacks; section modes
- [x] T017 [P] [US3] [US4] Tests: both reference packs on all 12 templates; widths 320, 375, 768, 1024, 1440, 1920 in LTR and RTL with no horizontal scroll; a throwing, slow and missing component leaves the rest working; axe in `ar` and `en`

## Phase 7: User Story 2, builder (P1)
- [x] T018 Builder feature: template chooser with previews and text descriptions, picker, schema-driven property forms, action menu and shortcuts for every placement operation, command stack for undo and redo, autosave states, previews by device and direction
- [x] T019 Builder checks: accessibility findings per placement and translation status per locale, blocking publish on blocking findings
- [x] T020 [P] [US2] Tests: keyboard-only and single-pointer composition, focus stays on moved placement and moves are announced, undo and redo order, offline autosave keeps edits locally

## Final phase
- [ ] T021 Performance budgets in CI (LCP, INP, CLS on reference profiles), frontend failure telemetry without content, OpenAPI, R1 implementation notes, spec evidence
