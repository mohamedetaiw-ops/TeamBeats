# Tasks: AI Evaluation Layer

Depends on 004, 006, 013 (`evaluation` use case), 020 and 027 (runtime, execution records, tool gateway sandbox mode); 012 (retrieval suites). R1 covers User Stories 1 to 6, 8 (calibration) and 10 (platform, tenant and agent levels).

## Phase 1: Foundations
- [ ] T001 `Evaluation` and `Evaluation.Contracts` projects; module map row; migration for suites, datasets, cases, personas, rubrics, runs, results and gates (forced RLS, platform tables as RLS exceptions)
- [x] T002 Permissions `eval.design`, `eval.review`, `eval.policy_manage`, `eval.view`; built-in role Evaluation Reviewer; audit actions of section 13 for R1
- [x] T003 Synthetic persona identities in `Identity`, excluded from sign-in and the directory

## Phase 2: User Stories 1 and 2, suites and datasets (P1)
- [x] T004 [P] [US1] [US2] Tests: inherited read-only suites; new version on save; thresholds below a higher level refused; cases on Workplace; persona runs with only its permissions; dataset versions kept as evidence; import validates before confirm
- [x] T005 [US1] Suites with levels and versions
- [x] T006 [US2] Datasets, cases, personas and import

## Phase 3: User Stories 3 and 4, engine and scoring (P1)
- [x] T007 [P] [US3] Tests: real runtime with sandbox doubles; deterministic metrics; judge through the gateway and different from the model under test; "not evaluated" without an eligible judge; trajectory match; denied action attempt fails as critical; background run with progress and cancel
- [x] T008 [US3] Runner on workers; deterministic, trajectory and judged evaluators
- [x] T009 [P] [US4] Tests: per-locale results and weighted score; critical failure fails the run; locales not averaged; noise margin; insufficient data
- [x] T010 [US4] Scoring

## Phase 4: User Story 5, release gates (P1)
- [ ] T011 [P] [US5] Tests: agent version gate with diff; no override for critical; blocking override rules by risk class; model route change gate; tool version gate; platform suites in CI
- [ ] T012 [US5] Gate requests and decisions for agent versions, model routes and tools; overrides; platform suites test project in CI

## Phase 5: User Stories 6, 8 and 10, dashboard, calibration and settings (P1)
- [ ] T013 [US6] Results, compare and case detail endpoints with org scope
- [ ] T014 [US8] Calibration sets, agreement and demotion of rubrics
- [ ] T015 [US10] Settings at platform, tenant and agent level with the strictest rule
- [ ] T016 Suite and dataset editors, run and compare views with table views, settings page, in English and Arabic, RTL; e2e with axe

## Final phase
- [ ] T017 Run time performance test; OpenAPI; R1 implementation notes; spec evidence
