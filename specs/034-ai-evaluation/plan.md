# Implementation Plan: AI Evaluation Layer

**Spec:** `specs/034-ai-evaluation/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0015 (`Evaluation` module, native .NET evaluators in R1), ADR-0011, spec 013 plan (`evaluation` use case), spec 027 plan (execution records, mandatory suites), spec 020 plan (gate requests)

## Summary
R1 builds the evaluation registry with versioned suites (User Story 1), golden datasets in Arabic and English with personas (User Story 2), the evaluation engine with deterministic, judged and trajectory checks (User Story 3), scoring against thresholds per locale (User Story 4), release gates for agent versions, model route changes, tools and retrieval configuration (User Story 5), the evaluation dashboard (User Story 6), judge calibration sets (User Story 8, R1 part), and configuration at platform, tenant and agent level (User Story 10). Continuous production evaluation, review queues, drift, A/B comparisons, business outcomes, organization level settings, French, Urdu and Hindi datasets, and external evaluator adapters are R2.

R1 slices, in order:
1. **Registry and datasets:** suites, versions, metrics, thresholds with their level, datasets, cases, personas, CSV and JSON import with validation.
2. **Runner:** runs on workers through the outbox; each case runs the agent on the real runtime as a synthetic persona identity with write tools on sandbox doubles; progress and cancel.
3. **Evaluators:** deterministic retrieval and citation metrics, tool and trajectory matching from execution records, judged metrics through the AI Gateway with versioned rubrics.
4. **Scoring and gates:** per-locale results, weighted score, severity, noise margin, minimum sample size; `EvaluationGateRequested` and `EvaluationGateDecided`; overrides for blocking failures.
5. **Dashboard and pages:** suite editor, dataset editor, run view, compare view with table views, settings, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Suites, datasets, runs, results and evidence tenant-owned with forced RLS; platform suites are platform data; workers shared, data not |
| III authorization | Yes | `eval.design`, `eval.review`, `eval.policy_manage`, `eval.view` through `IAuthorizationService`; results scoped to the viewer's org scope |
| VIII AI | Yes | Judge calls only through `IAiGateway` with use case `evaluation`; judge differs from the model under test |
| X, XXVIII evaluation before release | Yes | Gates block critical failures with no override; platform suites run in CI |
| Rule 9 | Yes | Datasets hold fictional content; personas are synthetic identities that cannot sign in |

## Module
A new `Evaluation` module with `Evaluation.Contracts` (ADR-0015). It is added to `docs/architecture/module-map.md` in this change.

## Data model (schema `evaluation`)
- `suite`, `suite_version` (metrics, weights, thresholds with level and severity, dataset versions, rubric versions), `dataset`, `dataset_version`, `case` (input, locale, persona, expectations, tags), `persona` (org unit, roles, membership type; backed by a synthetic membership flagged `test_identity` in `Identity`, excluded from sign-in and the directory), `rubric`, `rubric_version`, `calibration_label`, `run`, `case_result`, `gate_request`, `gate_decision`, `override`.
- Platform rows (suites for templates, mandatory safety suites, calibrated rubrics) live in platform tables (RLS exception) and are read-only to tenants.

## Sandbox doubles
Tool handlers of `write_self` and `write_external` tools have a sandbox mode selected by the run's evaluation flag: the gateway records the call and the double returns a canned result, so nothing changes. Read tools run for real as the persona.

## Gate flow
- Agent versions: spec 020 publishes `EvaluationGateRequested(agentVersion)`; the gate runs mandatory safety suites, inherited platform suites and the agent's suites; `EvaluationGateDecided` carries pass, blocking failures and critical failures.
- Model route changes: spec 013 publishes `AiModelRouteChangeProposed`; the gate runs the suites of every published agent using the affected use cases against the proposed route.
- Tool versions and retrieval configuration: the same request with the affected agents' trajectory or retrieval suites (retrieval against a shadow index once planned spec 012 exists).
- Platform CI: a test project runs platform suites against the stub or fake provider on every build, failing on critical regressions.

## Scoring rules
Strictest threshold of all levels; locale results never averaged; blocking metrics need at least 20 cases per locale; regression only beyond the suite's measured noise margin (from repeated baseline runs); judged rubrics need a calibration set (default 50 per locale) with at least 85% agreement before they can be blocking.

## API (v1)
- Suites and datasets: `GET/POST /api/v1/evaluation/suites`, `PUT .../{id}/draft`, `GET/POST /api/v1/evaluation/datasets`, `POST .../{id}/import` (validate, then confirm).
- Runs: `POST /api/v1/evaluation/runs`, `GET .../{id}`, `POST .../{id}/cancel`, `GET /api/v1/evaluation/compare?a=&b=`.
- Gates: `GET /api/v1/evaluation/gates/{id}`, `POST .../{id}/override {reason}`.
- Settings: `GET/PUT /api/v1/evaluation/settings`.

## Tests
- Integration per user story: US1 scenarios 1 to 5, US2 scenarios 1 to 4 and 6, US3 scenarios 1 to 7, US4 scenarios 1 to 5, US5 scenarios 1 to 4, 6 and 7 (5 with search), US6 scenarios 1 to 5, US8 scenarios 1, 3 and 4 (calibration), US10 scenarios 1 to 3.
- Boundaries: datasets, runs and results of another tenant not found; org-scoped viewers limited; personas cannot sign in or appear in the directory.
- Performance: a 100 case run completes within the time in section 15 against the fake provider.
- Acceptance (e2e): edit a suite, import cases, run, read and compare results, in English and Arabic with axe.
