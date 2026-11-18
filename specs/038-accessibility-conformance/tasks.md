# Tasks: WCAG 2.2 AA Accessibility Conformance

Depends on 007 (baseline), 008 (shell and preferences), 032 (page builder), 033 (modes), 036 (design suite).

## Phase 1: Spec and gate (US1)
- [x] T001 Spec 038, plan, tasks; spec 007 FR-018 and section 10, spec 036 section 10, constitution XXIV verification line, specs index, `docs/testing/e2e-plan.md`, `docs/design-system/testing.md`
- [x] T002 `a11y` Playwright project and `accessibility` CI job in two shards; `e2e` job runs only the `chromium` project
- [x] T003 Route inventory `a11y/routes.ts` and the inventory test over `src/Web/app` (A11Y-004)
- [x] T004 Exclusions file with reason and review date, and its test (A11Y-008)
- [x] T005 Matrix suite: primary pages in 32 combinations, other routes rotated (A11Y-005, A11Y-006)

## Phase 2: Fixes found by the gate (US1)
- [x] T006 Shell and shared parts: every serious and critical finding fixed in `components/ui`, shell and global CSS
- [x] T007 Primary pages: findings fixed in their feature code
- [ ] T008 Other routes, grouped by module

## Phase 3: Real-page checks (US2, US3)
- [x] T009 Skip link, focus indicator, no positive tabindex, focus not obscured (A11Y-009, A11Y-010)
- [x] T010 Target size, reflow at 320 pixels, 200 percent text, text spacing, reduced motion on primary pages (A11Y-011 to A11Y-015)
- [x] T011 Overlay focus trap, Escape and return in the component gallery (A11Y-016)
- [x] T012 `jsx-a11y` recommended rules at error level and the fixes they need (A11Y-017)
- [x] T013 Token contrast checks cover focus ring and status colours in every design and mode, on base and raised surfaces (A11Y-018)

## Phase 4: Interaction rules (US4, US5)
- [x] T014 Non-drag move controls with announcements: page builder, home arrange mode, process modeler, boards (A11Y-019)
- [x] T015 Sign-in fields: autocomplete and paste, checked by `a11y/authentication.spec.ts` (A11Y-022)
- [ ] T021 Redundant entry review of multi-step forms (A11Y-021)

## Phase 5: Statement and report (US6)
- [x] T016 `/accessibility` statement page, public, ar and en, footer fallback link (A11Y-023)
- [x] T017 Conformance map `docs/accessibility/wcag-2.2-conformance.md` and report `docs/accessibility/conformance-report.md` (A11Y-001, A11Y-002)

## R3
- [ ] T018 External assistive technology audit and report update (A11Y-024)
- [ ] T019 Tagged PDF output for letters and reports
- [ ] T020 Accessibility matrix in Firefox and WebKit
