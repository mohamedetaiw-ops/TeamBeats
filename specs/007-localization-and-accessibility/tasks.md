# Tasks: Localization and Accessibility Foundation

Depends on 001 Phase 1 (T005 Next.js skeleton). Start early: every UI task in 001 to 006 uses this.

## Phase 1: Foundations
- [ ] T001 Install and configure `next-intl`; catalogues for ar, en, fr, ur, hi (fr/ur/hi marked pending)
- [ ] T002 Backend localization building block sharing the key namespace
- [ ] T003 Self-hosted OFL fonts (Noto Sans Arabic, Noto Nastaliq Urdu, Noto Sans Devanagari, Inter), subset, per-locale loading

## Phase 2: User Story 1, language and direction (P1)
- [ ] T004 [P] [US1] Tests: resolution matrix; `lang` and `dir` set; preference saved
- [ ] T005 [US1] Locale resolver middleware (web) and resolver (API, emails, Problem Details)
- [ ] T006 [US1] Language switcher in header and profile

## Phase 3: Lint and CI gates
- [ ] T007 [P] ESLint rule for literal JSX text; stylelint rule for physical direction properties
- [ ] T008 [P] Missing-key CI gate for launch-complete locales (ar, en)
- [ ] T009 Pseudo-locales `en-XA` and `ar-XB` in Development/Test

## Phase 4: User Story 2, mixed direction (P1)
- [ ] T010 [P] [US2] Bidi fixture tests (Arabic with English names and numbers)
- [ ] T011 [US2] `<bdi>` / `dir="auto"` handling in text primitives

## Phase 5: User Story 5, accessibility (P1)
- [ ] T012 [US5] Design-system primitives: Button, Icon (mirrorInRtl), Tree, Table, Dialog, FormField, Toast
- [ ] T013 [P] [US5] Playwright + axe on every R0 screen (zero critical); keyboard walkthrough tests
- [ ] T014 [P] [US5] RTL screenshot tests for sign-in and admin shell in ar

## Phase 6: User Story 4, formatting (P2)
- [ ] T015 [P] [US4] Tests: Western digits default, tenant switch to Arabic-Indic, user time zone fallback to tenant
- [ ] T016 [US4] Formatting helpers on `Intl` with tenant `numberingSystem`

## Phase 7: User Story 3, content variants (P2)
- [ ] T017 [US3] Publish the content variant contract for spec 009 (no content storage in R0)

## Final phase
- [ ] T018 Native-speaker review checklist for ar and en before R1; Nastaliq performance test scheduled before R2
