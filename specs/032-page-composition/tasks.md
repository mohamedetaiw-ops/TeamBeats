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
- [x] T021 Performance budgets in CI (LCP, INP, CLS on reference profiles), frontend failure telemetry without content, OpenAPI, R1 implementation notes, spec evidence
- [x] T022 One rendering and one instance per data source (FR-070 to FR-073): the data source key and view in the manifest, the validator warning on both placements, the renderer leaving out the later duplicate and recording it, placement states in the widget frame, the content budget by region width, and the budget, views list and supported states in the manifest with refusal at registration (reasons `budget`, `views`, `states`), tested at quarter, half and full width

## R2: Governance, fragments and portability

R2 covers User Story 6 approval, impact and scheduling, User Story 7 independent deployment, and FR-007, FR-022, FR-033 and FR-045. Approval routes run on the spec 024 engine and land in the spec 016 inbox, as for agent approvals.

- [x] T023 [US6 scenario 1] Approval routes for pages: a route per site or scope (`page.approve`), submit for approval with a visual and structural diff for the approver, approve or return with a comment, an editor never approves their own change; inbox task source; tests including a scoped approver of another organization
- [x] T024 [US6 scenario 2] Impact reports: before a shared layout, global page area or template change is published, the affected organizations, audiences, pages and the count of personal layouts touched, with cancel; also used by theme publishing in spec 033
- [x] T025 [US6 scenario 4] Scheduled releases: go live and expire at tenant-local times; validation again at go-live, a failed release does not go live, the publisher is notified and the current release stays; tests with the clock
- [x] T026 [US6 scenario 6] Edit conflicts: a second save gets both versions and a merge or overwrite choice; nothing lost
- [x] T027 Fragments (FR-045): save a section or region pattern as a governed fragment with owner, scope and version; apply to new pages as references, never copied component code; tests for scope and another tenant
- [x] T028 Template import and export (FR-022): documented JSON with structure and policies and no secrets, tenant IDs or personal data; import validates as a new draft
- [x] T029 Resizable regions (FR-007): editors resize regions within declared constraints, persisted, never overlapping, by pointer and keyboard
- [x] T030 Feature controls (`feature_control.manage`): tenant and scope switches for components and templates as versioned configuration, read by the picker and the renderer
- [x] T031 [US7] Independent component deployment (FR-033): a registered package version is deployable and updatable without rebuilding page definitions, migration hooks run on upgrade, pages that do not use it are untouched
- [x] T032 Builder pages for approval, schedule, impact, fragments and import, in `ar` and `en`, RTL; OpenAPI, R2 implementation notes, e2e with axe

## R3
R3 covers FR-023 template recommendations and FR-031 tenant-built components in a sandbox (ADR-0012 point 6).

- [x] T033 Template recommendations (FR-023): rules on purpose, viewport and density over released platform templates, advice only, feature controls respected, checked facts, `page.edit` coverage; tests
- [x] T034 "Help me choose" on the new page screen: recommended badge with reasons, every template stays available, the variant follows the advice; e2e in `ar` and `en` with axe; plan, notes, evidence, OpenAPI
- [ ] T035 Tenant components: submission with manifest checks and document size, immutable versions, platform security review with approve and reject, audit, capability; tests including another tenant and the package
- [ ] T036 Sandboxed rendering: the served document with its own policy (opaque origin, declared hosts only), the frame placement with the message contract (declared context, tokens, properties in; height and declared events out), three per page; tests
- [ ] T037 Pages: tenant components in the admin component area, the review queue on the platform host, the sandbox placement in the builder and renderer; e2e in `ar` and `en` with axe; notes and evidence

