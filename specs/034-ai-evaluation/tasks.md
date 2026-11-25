# Tasks: AI Evaluation Layer

Depends on 004, 006, 013 (`evaluation` use case), 020 and 027 (runtime, execution records, tool gateway sandbox mode); 012 (retrieval suites). R1 covers User Stories 1 to 6, 8 (calibration) and 10 (platform, tenant and agent levels).

## Phase 1: Foundations
- [x] T001 `Evaluation` and `Evaluation.Contracts` projects; module map row; migration for suites, datasets, cases, personas, rubrics, runs, results and gates (forced RLS, platform tables as RLS exceptions)
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
- [x] T011 [P] [US5] Tests: agent version gate with diff; no override for critical; blocking override rules by risk class; model route change gate; tool version gate; platform suites in CI
- [x] T012 [US5] Gate requests and decisions for agent versions, model routes and tools; overrides; platform suites test project in CI

## Phase 5: User Stories 6, 8 and 10, dashboard, calibration and settings (P1)
- [x] T013 [US6] Results, compare and case detail endpoints with org scope
- [x] T014 [US8] Calibration sets, agreement and demotion of rubrics
- [x] T015 [US10] Settings at platform, tenant and agent level with the strictest rule
- [x] T016 Suite and dataset editors, run and compare views with table views, settings page, in English and Arabic, RTL; e2e with axe

## Final phase
- [x] T017 Run time performance test; OpenAPI; R1 implementation notes; spec evidence

## R2: Production evaluation, review queues, comparisons and outcomes

R2 covers User Stories 7 and 9, US8 scenarios 2 and 3, US10 organization level, US1 scenarios 5 and 6 and US3 scenario 8, and FR-023 to FR-027. Capability keys `ai.evals.production`, `ai.evals.business_outcomes` and `ai.evals.experiments` gate each part.

- [x] T018 [US7] Continuous production evaluation (FR-023): sampling by tenant policy (default 2% of agent runs, at most 1000 per agent per day) after a run completes, asynchronous through the outbox; reference-free metrics (groundedness, citation correctness, relevance, refusal correctness, trajectory checks); Restricted content gets metadata metrics only; Confidential needs a judge eligible under spec 013 routing; metadata-only logging stores scores and verdicts only; opted-out people and disabled tenants never sampled; optional sample retention for Public and Internal content up to 30 days; tests for each rule and another tenant
- [x] T019 [US7 scenario 6, US3 scenario 8] Drift detection (FR-024): rolling window (default 7 days) against the last gate result; alerts to the agent owner and the AI Policy Administrator with metrics and examples; a silent model change caught by the nightly or production evaluation offers pinning or switching the route
- [x] T020 [US8 scenarios 2 and 3] Review queues (FR-025): flagged cases (low score, negative feedback, judge uncertain, policy denial) to an evaluation reviewer in scope through the inbox, content shown under the reviewer's own permissions; overturning a verdict corrects the result and counts toward rubric agreement; promotion to a dataset after redaction with the source recorded (US1 scenario 5)
- [x] T021 [US9 scenarios 2 and 3] A/B and champion and challenger comparisons (FR-026): AI Policy Administrator approval, the challenger must have passed its gate, a set share of eligible runs with stable allocation per user, both arms on the same metrics, stop returns everyone to the champion
- [x] T022 [US9 scenario 1] Business outcome scoring (FR-027): tenant baselines per task type; completion, escalation, resolution without human help and estimated time saved per agent with the spec 021 minimum group size; never per employee
- [x] T023 [US10] Organization level settings: business-specific datasets and stricter thresholds for a unit and below, the strictest rule applied
- [x] T024 [US1 scenario 6] French, Urdu and Hindi datasets and platform cases for tenants with those locales (with spec 007 R2)
- [x] T025 Pages for production results, drift alerts, the review queue, comparisons and outcomes, in `ar` and `en`, RTL; OpenAPI, R2 implementation notes, e2e with axe, spec evidence

## R3: L4 process runs and estate trends

R3 covers the R3 part of the target release: evaluation of L4 process runs end to end (with spec 027 R3) and cross-version trend benchmarks across the agent estate (US6 at estate level).

- [x] T026 L4 process runs: `AgentTaskRunRecorded` from Agents, deterministic trajectory checks per step (scope, approval, limit, denials, completion), critical failures, instance outcome from process events, results per process version in scope; tests including cross-tenant
- [ ] T027 Estate trends under `ai.evals.advanced`: per agent and version headline metrics, regressions beyond the noise margin, monthly estate benchmarks per metric and locale; tests including scope, package and cross-tenant
- [ ] T028 Pages (estate trends with a table view, L4 panel on the process page), OpenAPI, R3 implementation notes, e2e with axe, spec evidence

