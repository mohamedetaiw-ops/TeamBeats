# Feature Specification: AI Evaluation Layer

| Field | Value |
|---|---|
| Spec ID | 034-ai-evaluation |
| Area | ai |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (evaluation registry, golden datasets in Arabic and English, evaluation engine with deterministic, judge and trajectory checks, scoring and thresholds, evaluation dashboard, release gates), R2 (continuous production evaluation, human review queues, drift detection, A/B and champion and challenger comparisons, business outcome scoring, French, Urdu and Hindi datasets, external evaluator adapters), R3 (cross-version trend benchmarks across a tenant's whole agent estate, evaluation of L4 process runs end to end) |
| Last updated | 2026-11-05 |
| Requirement IDs | EVL-001 to EVL-030 (new), AI-008 (quality part), AI-010 (evidence part), UC-07, UC-13 |
| Use cases | UC-06, UC-07, UC-13 |
| Depends on | ADR-0015 (evaluation architecture), ADR-0011 (agent runtime), 013 (AI Gateway), 014 (assistant and RAG), 020 (Agent Hub), 027 (agent governance), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization), 016 (task inbox), 021 (analytics), 026 (residency) |
| Used by | 020 (publishing gate), 027 (mandatory safety suites), 013 (model route changes), 014 and planned 012 (retrieval quality), 021 (AI quality figures, product experience evaluation) |

## 1. Problem and outcome

**Problem:** An agent that passed review last month can get worse today because the provider updated the model, someone edited the instructions, a connector changed its response, or the knowledge library was re-indexed with a new chunking rule. Nobody notices until an employee gets a wrong leave balance or sees a document they should not. Buyers in government and regulated industries will also ask for proof: how do you know this HR agent answers correctly in Arabic, never leaks salaries, and resists injection, and what changed between version 1.3 and 1.4. A review by a person before publishing does not answer this, and neither do traces alone.

**Outcome:** TeamBeats has a dedicated AI Evaluation Layer as a core platform capability in every package. Every AI capability (agents, the assistant, retrieval, models and tools) has versioned evaluation suites with golden datasets, scoring rules and thresholds. Suites run before any change to a model, prompt, agent, connector tool or RAG configuration is promoted, and promotion is blocked when a critical threshold fails. Results are kept as evidence, linked to the exact versions tested, and shown on a dashboard that compares versions, models, languages, cost and speed. From R2, a privacy-safe sample of real interactions is evaluated continuously to catch drift. Each tenant owns its own datasets, thresholds and results, inside its isolation and residency boundary.

### Three connected concerns

| Concern | Question it answers | Where it lives |
|---|---|---|
| Governance | What is the AI allowed to do? | Spec 027 (autonomy, tool gateway, approvals), spec 013 (model policy) |
| Observability | What happened during execution? | Spec 027 execution records, spec 013 metering, OpenTelemetry traces (constitution XIX) |
| Evaluation | Does the AI capability behave correctly and meet its quality targets? | This spec |

The three are designed separately and connected by contracts: evaluation reads traces and execution records, and governance reads evaluation results at publish time. Evaluation never replaces a governance control. Evaluation is not a synchronous step in user requests: the safety checks that must run on every request (classification routing, tool gateway checks, injection handling, output handling) stay inline in specs 013 and 027. Heavier quality evaluation runs asynchronously.

### Two kinds of evaluation

- **AI evaluation (this spec):** quality, accuracy, safety, reliability, cost and business effectiveness of agents, retrieval, models and tool executions. Built first.
- **Product experience evaluation:** whether search ranking, personalization, recommendations and employee journeys deliver useful results. Owned by spec 021 (ANL-023, ANL-024) and planned spec 012, reusing the dataset, run and scoring services of this spec. R2 and R3.

## 2. Scope

**In scope:**
- Evaluation registry: suites per AI capability, versioned.
- Dataset management: golden questions, expected outcomes, expected sources, expected trajectories and attack scenarios, versioned, per locale.
- Evaluation engine: deterministic checks, model-based (judge) checks, retrieval metrics and trajectory checks, run asynchronously on workers.
- Scoring and thresholds: per-metric thresholds, weighted quality scores, severity (critical, blocking, warning), per-locale results.
- Evaluation dashboard: compare agent versions, models, languages, cost and performance.
- Release gates: block promotion of agent versions, model route changes, prompt and platform instruction changes, tool and connector versions, and RAG configuration changes that fail critical thresholds.
- Continuous production evaluation on sampled interactions (R2).
- Human review queues and judge calibration (R1 calibration sets, R2 review queues).
- Drift detection and A/B or champion and challenger comparisons (R2).
- Business outcome scoring (R2).
- Multi-level configuration: platform, tenant, organization, agent, agent version.
- Evaluation evidence store with immutable results and audit.
- Adapters to external evaluation tools (ADR-0015).

**Out of scope:**
- Policy enforcement at run time (specs 013 and 027). Evaluation measures, governance enforces.
- Execution records and metering as systems of record (specs 027 and 013). Evaluation references them.
- Training or fine-tuning models.
- Product experience evaluation dashboards (spec 021), beyond the shared services they reuse.
- Evaluation of individual employees. Evaluation scores AI behavior, never people.

## 3. Actors and permissions

Permission keys follow spec 004 FR-013 and are separate from the `ai.evals.*` capability keys in section 9.

| Actor | What they can do | Permission / scope |
|---|---|---|
| Agent designer | Create and edit suites and datasets for agents in their scope, run evaluations, read results | `eval.design` on an org scope (included in Agent Designer) |
| Agent owner | Read results and alerts for their agents, request reruns | Named owner of the agent |
| Evaluation reviewer | Label cases in human review queues and calibration sets, confirm or overturn judge verdicts | `eval.review` (built-in role Evaluation Reviewer) |
| Agent reviewer / publisher | See gate results at review, approve overrides of blocking (never critical) thresholds | `agent.publish` |
| AI Policy Administrator | Set tenant evaluation policy, thresholds, judge models, sampling rules; read all results | `eval.policy.manage` at tenant scope (included in AI Policy Administrator) |
| Organization administrator | Tighten thresholds and add datasets for their unit | `eval.policy.manage` at org scope, restrict only |
| Security officer | Read all results, failures and evidence; export evidence | `eval.view`, `audit.read` |
| Platform operator | Maintain the platform framework, default policies, platform suites and datasets for templates, platform release gates | Platform |

## 4. User scenarios

### User Story 1: Define an evaluation suite for an agent (Priority: P1, R1)

Sara installs the HR Assistant template. It arrives with the platform suite for that template (leave policy questions, eligibility, unsupported requests, ambiguous questions, unauthorized data access) in Arabic and English. She adds 40 cases for Acme's own leave rules and sets the quality target for her agent.

**Independent test:** install a template, see its inherited suites, add tenant cases, run the suite, see per-case and per-locale results stored against the draft version.

**Acceptance scenarios:**
1. **Given** a template install, **When** the agent is created, **Then** it inherits the template's platform suites as read-only and the mandatory safety suites of spec 027 as read-only, and gets an empty tenant suite Sara can edit.
2. **Given** a suite, **When** Sara opens it, **Then** she sees its metrics, thresholds with their source level (platform, tenant, org, agent), dataset versions and last results.
3. **Given** Sara edits the tenant suite, **When** she saves, **Then** a new suite version is created; results always name the suite version they used.
4. **Given** she tries to lower a threshold below the tenant or org minimum, or to remove a mandatory suite, **When** she saves, **Then** the save is refused with the rule and its level named.
5. **Given** a Workplace tenant, **When** Sara adds cases, **Then** she can, because adding cases to an agent's own suite is part of the baseline in every package.

### User Story 2: Manage golden datasets (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the dataset editor, **When** Sara adds a case, **Then** she enters the input (question or task), locale, persona (a test user profile with org unit and permissions), and one or more expectations: expected answer or key facts, expected sources, sources that must not appear, expected tool calls and parameters, expected refusal or escalation, and tags (for example "eligibility", "ambiguous", "unauthorized").
2. **Given** a persona, **When** the case runs, **Then** it runs as a synthetic test identity with exactly that persona's permissions inside the tenant, never as Sara and never as an administrator, and test identities cannot sign in or appear in the directory.
3. **Given** a dataset version is used in a release gate result, **When** someone edits the dataset, **Then** a new version is created and the old version stays unchanged as evidence.
4. **Given** a CSV or JSON import of 200 cases, **When** it is uploaded, **Then** each row is validated, invalid rows are listed with reasons, and nothing is imported until the user confirms.
5. **Given** a case built from a real conversation (R2, from a review queue), **When** it is saved to a dataset, **Then** personal data is redacted first and the case records where it came from.
6. **Given** an R1 tenant, **When** Sara picks locales, **Then** Arabic and English are offered. French, Urdu and Hindi datasets are offered from R2 for tenants with those locales enabled.

### User Story 3: Run evaluations with deterministic, judge and trajectory checks (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a suite run, **When** each case executes, **Then** the agent runs on the real runtime (ADR-0011) through the AI Gateway and the tool gateway, with write tools executed against sandbox doubles that record the call and change nothing, and the run produces a normal execution record (spec 027) marked as an evaluation run.
2. **Given** deterministic metrics, **When** a case completes, **Then** the engine checks without any model: expected sources retrieved (recall@K, precision@K), forbidden sources absent, citations point to sources the persona can read and that contain the cited passage, expected tool calls and parameters, no tool call outside the allowlist, refusal or escalation present when expected, latency and cost.
3. **Given** a judged metric (relevance, correctness against the expected answer, completeness, groundedness of each claim, tone, language correctness), **When** it is scored, **Then** an independent judge model is called through the AI Gateway under use case `evaluation`, with a versioned rubric, and the judge's verdict, reason and model are stored with the case.
4. **Given** the judge model, **When** it is chosen, **Then** it must differ from the model under test, must be allowed by tenant policy for the classification of the case content, and if no judge model is eligible, judged metrics show "not evaluated" and only deterministic metrics count.
5. **Given** a trajectory expectation, **When** the run is scored, **Then** the engine compares the actual steps from the execution record with the expected ones: tools chosen and their order where order matters, parameters, approvals and confirmations requested, retries, handoffs, and any attempted action that the tool gateway denied.
6. **Given** a trajectory with an unauthorized action attempt, **When** it is scored, **Then** the case fails as critical even if the tool gateway blocked the action and the final answer was correct.
7. **Given** a run of 100 cases, **When** it is started, **Then** it runs in the background, shows progress, can be cancelled, and completes within the time in section 15.

### User Story 4: Score against thresholds (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a suite with metrics and weights, **When** a run ends, **Then** each metric shows its value per locale and overall, the weighted quality score is shown, and each threshold shows pass or fail with its severity.
2. **Given** a critical threshold (for example zero unauthorized disclosures, zero successful injections, zero blocked operations executed), **When** one case breaks it, **Then** the run fails, whatever the other scores are.
3. **Given** Arabic scores 82% and English 96% for answer correctness with a threshold of 85%, **When** the run is scored, **Then** it fails for Arabic. Locale results are never averaged to hide a failing locale.
4. **Given** judged metrics vary between runs, **When** a version is compared with its baseline, **Then** a regression is reported only when the drop is larger than the noise margin measured for that suite, and the result shows the sample size.
5. **Given** a suite with fewer cases than the minimum for a metric (default 20 per locale for blocking metrics), **When** it runs, **Then** the metric is reported as "insufficient data" and cannot pass a blocking gate on its own.

### User Story 5: Block a release that fails (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** an agent version submitted for review (spec 020 US2), **When** the gate runs, **Then** the mandatory safety suites of spec 027, the inherited platform suites and the agent's own suites run, and the reviewer sees the gate result, the diff of scores against the published version, and failing cases.
2. **Given** any critical threshold fails, **When** anyone tries to publish, **Then** publishing is blocked and no role can override it.
3. **Given** only a blocking (non-critical) threshold fails on a Low or Medium risk agent, **When** a publisher with `agent.publish` who is not the last editor overrides it with a written reason, **Then** the version can be published, the override is audited, and the agent owner gets a follow-up task. High risk agents cannot be overridden.
4. **Given** the AI Policy Administrator changes the default model for the assistant use case (spec 013), **When** saved, **Then** the gate runs the suites of every published agent and capability using that use case against the new model first, and the change applies only when no critical threshold fails; blocking failures are listed and the administrator can apply with a reason.
5. **Given** a change to retrieval configuration (embedding model, chunking rule, number of results, reranking; spec 014 and planned 012), **When** it is proposed, **Then** the retrieval suites run against a shadow index and the change is promoted only if recall@K and groundedness meet thresholds.
6. **Given** a new version of a tool or connector action (spec 027 tool registry, spec 017), **When** it is registered, **Then** the trajectory suites of the agents that hold that tool run before the new version is enabled for them.
7. **Given** a platform change (platform safety instructions, a template version, a model added to the catalogue, a platform prompt), **When** the platform release pipeline runs, **Then** the platform suites run in CI and the release is blocked on critical failures (constitution XXVIII).
8. **Given** a provider changes the model behind the same name without notice, **When** the nightly or production evaluation detects a regression (R2), **Then** the owner and the AI Policy Administrator are alerted and the model route can be pinned or switched.

### User Story 6: Compare versions on the evaluation dashboard (Priority: P1, R1)

Layla opens HR Policy Agent version 1.4 and sees task success 94%, groundedness 98%, zero critical leaks, answer relevance 96%, tool execution accuracy 91%, citation correctness 98% and multilingual quality 88%, compared with version 1.3 and with the same version on a cheaper model. (Illustrative figures.)

**Acceptance scenarios:**
1. **Given** an agent, **When** Layla opens its evaluation page, **Then** she sees headline metrics for the current version, pass or fail per gate, and trends across versions.
2. **Given** two runs, **When** she compares them, **Then** she sees metric differences by locale, model, cost per successful task and p50 and p95 latency, and the cases that changed from pass to fail and the reverse.
3. **Given** a failing case, **When** she opens it, **Then** she sees input, expected outcome, actual answer (subject to the logging rules in section 11), sources, trajectory, judge reasoning and the link to the execution record.
4. **Given** an organization administrator, **When** they open the dashboard, **Then** they see only agents and results in their scope.
5. **Given** a screen reader user, **When** they read a chart, **Then** a table view and text summary are available (spec 007).

### User Story 7: Continuous production evaluation (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** tenant sampling policy (default 2% of agent runs, at most 1,000 per agent per day), **When** a run completes, **Then** a sampled run is evaluated asynchronously with the reference-free metrics (groundedness against retrieved sources, citation correctness, relevance, refusal correctness, trajectory policy checks), and the user's request is never slowed.
2. **Given** a run whose content is Restricted, **When** sampling decides, **Then** it is excluded from content-based evaluation; only metadata metrics (latency, cost, tool outcomes, denials) are used.
3. **Given** Confidential content, **When** it is evaluated, **Then** the judge model must be eligible for Confidential under spec 013 routing (for example a private endpoint); otherwise only deterministic metrics run.
4. **Given** the tenant's logging policy is metadata only (spec 013 US8), **When** a sampled run is evaluated, **Then** the evaluator reads the content once inside the deployment boundary, stores scores and verdicts, and stores no prompt or answer text; content is kept only when the tenant has enabled evaluation sample retention for Public and Internal content, for at most 30 days.
5. **Given** a user who opted out of AI feedback collection or a tenant that disabled production evaluation, **When** their runs complete, **Then** they are not sampled.
6. **Given** a quality score that drops below its alert threshold over a rolling window (default 7 days), **When** drift is detected, **Then** the agent owner and the AI Policy Administrator are alerted with the affected metrics and examples.

### User Story 8: Human review and judge calibration (Priority: P1, R1 calibration, R2 review queues)

**Acceptance scenarios:**
1. **Given** a judged rubric, **When** it is created, **Then** it needs a calibration set labelled by people (default 50 cases per locale) and its agreement with human labels (default at least 85%) before it can be used for a blocking threshold. Platform rubrics ship calibrated for Arabic and English.
2. **Given** a review queue (R2), **When** production evaluation flags a case (low score, negative user feedback, judge uncertain, policy denial), **Then** it goes to an evaluation reviewer within scope as a task in the inbox (spec 016) with content shown under the reviewer's own permissions.
3. **Given** a reviewer overturns a judge verdict, **When** they save, **Then** the result is corrected, the disagreement counts toward the rubric's agreement score, and the case can be promoted to a golden dataset after redaction.
4. **Given** a rubric whose agreement falls below the target, **When** measured, **Then** it is demoted to warning severity until recalibrated, and the owner is told.

### User Story 9: Business outcomes and comparisons (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** business outcome scoring, **When** the tenant sets a baseline per task type (for example 12 minutes to raise a VPN request without AI), **Then** the dashboard shows completion rate, escalation rate, resolution without human help, and estimated time saved per agent, aggregated with the minimum group size of spec 021.
2. **Given** a challenger version or model, **When** an A/B or champion and challenger comparison is approved by the AI Policy Administrator, **Then** a set share of eligible runs uses the challenger (stable per user), both arms are evaluated with the same metrics, and stopping the comparison returns all users to the champion.
3. **Given** a High risk agent, **When** someone proposes an online comparison, **Then** the challenger must first pass the full release gate; comparisons never bypass the gate.

### User Story 10: Configure evaluation at each level (Priority: P1, R1 platform, tenant and agent, R2 organization)

| Level | Owns |
|---|---|
| Platform | Evaluation framework, metric definitions, default policies and thresholds, mandatory safety suites, platform suites and datasets for templates, calibrated platform rubrics |
| Tenant | Data residency of evaluation data, judge model choice within model policy, sampling and retention rules, tenant thresholds (only stricter than platform), tenant-wide datasets |
| Organization | Business-specific datasets, stricter thresholds for its unit and below |
| Agent | Its suites, quality targets and weights, within the levels above |
| Agent version | Its results, regression comparison against the previous version, release decision |

**Acceptance scenarios:**
1. **Given** the platform sets groundedness at 90% and the tenant sets 95%, **When** an agent owner sets 92%, **Then** the save is refused; the effective threshold is the strictest of all levels.
2. **Given** a tenant, **When** it opens evaluation settings, **Then** it cannot turn off mandatory suites, critical thresholds or the release gate.
3. **Given** two tenants on a shared installation, **When** evaluations run, **Then** each tenant's datasets, runs, scores and traces are separated by tenant under RLS and stored in the tenant's data region (spec 026); the evaluation workers are shared, the data is not.

### Edge cases
- The model under test and the only allowed judge are the same model: judged metrics show "not evaluated, no independent judge", and the gate requires a human review of a sample instead, or the tenant allows another judge model.
- A case's expected source was deleted or the persona lost access: the case is marked stale, excluded from the score and listed for the owner.
- An evaluation run exceeds the tenant's AI budget: the run pauses with a notice; mandatory platform suites for a release gate are charged to the platform, not to the tenant budget.
- A provider is down during a gate: the gate is "incomplete", never "passed"; the change waits or is retried.
- A tenant imports cases that contain Restricted content: the dataset takes the highest classification and only judges eligible for that classification are used.
- A suite is edited during a run: the run continues on the version it started with.
- An agent is suspended (spec 027): scheduled evaluations stop; existing results remain.

## 5. Functional requirements

- **EVL-001 / FR-001**: The platform MUST provide an Evaluation module as its own bounded context with its own APIs, persistence, policies and background workers (ADR-0015). Other modules MUST use its published contracts and events only (constitution XIV).
- **EVL-002 / FR-002**: The module MUST be organized as a control plane (suites, datasets, rubrics, thresholds, policies, permissions, dashboards), an execution plane (runners, evaluators, scoring, asynchronous jobs, regression comparison) and an evidence store (versioned results, trace references, scores, reviewer decisions, gate decisions, audit).
- **EVL-003 / FR-003**: Evaluation MUST NOT be a synchronous step in user-facing AI requests. Safety checks that must run on every request MUST stay inline in specs 013 and 027.
- **EVL-004 / FR-004**: The registry MUST hold versioned suites for every AI capability: each agent, the assistant and routing (014, 020 US7), retrieval configurations, model routes per use case, and tools with write effects. Each suite MUST name its capability, metrics, thresholds, datasets and owner.
- **EVL-005 / FR-005**: Datasets MUST be versioned and immutable once used by a recorded result. Cases MUST support input, locale, persona, expected answer or key facts, expected and forbidden sources, expected trajectory, expected refusal or escalation, tags and severity.
- **EVL-006 / FR-006**: Cases MUST run as synthetic test identities with the persona's permissions inside the tenant. Test identities MUST NOT be able to sign in, appear in the directory, receive notifications or hold administrator roles.
- **EVL-007 / FR-007**: Evaluation runs MUST use the production runtime, AI Gateway and tool gateway, with write tools bound to recording sandbox doubles. Evaluation runs MUST be marked as such in execution records and metering.
- **EVL-008 / FR-008**: The engine MUST provide deterministic evaluators for retrieval (recall@K, precision@K, reciprocal rank), citation correctness, forbidden source detection, tool selection and parameter accuracy, refusal and escalation, latency and cost, with no model call.
- **EVL-009 / FR-009**: Judged metrics MUST use a versioned rubric, an independent judge model different from the model under test, called through the AI Gateway under use case `evaluation`, and MUST store verdict, reason, judge model and rubric version.
- **EVL-010 / FR-010**: Security and correctness gates MUST NOT rely on judged metrics alone. Critical thresholds MUST be computed by deterministic evaluators or human-calibrated rubrics.
- **EVL-011 / FR-011**: A rubric MUST reach the tenant's agreement target against a human-labelled calibration set before it can drive a blocking threshold, and MUST drop to warning severity when agreement falls below target.
- **EVL-012 / FR-012**: Trajectory evaluation MUST read the execution record of spec 027 and score tool choice, order, parameters, approvals, confirmations, retries, handoffs and denied or unauthorized attempts. An unauthorized attempt MUST fail the case as critical.
- **EVL-013 / FR-013**: The evaluation dimensions in section 12 MUST be supported with the default metrics and thresholds listed there.
- **EVL-014 / FR-014**: Thresholds MUST have a severity of critical, blocking or warning. Critical failures MUST block promotion with no override. Blocking failures MAY be overridden for Low and Medium risk agents by a user with `agent.publish` who is not the last editor, with a reason. High risk agents MUST NOT be overridden.
- **EVL-015 / FR-015**: Scores MUST be reported per locale, and each locale MUST meet its thresholds on its own.
- **EVL-016 / FR-016**: Regression comparison MUST report differences against the baseline version with sample size and a noise margin from repeat runs, and MUST list cases whose outcome changed.
- **EVL-017 / FR-017**: Release gates MUST run before promotion of: agent versions (020), changes to the model behind a use case or fallback chain (013), platform safety instructions and platform templates, prompt or instruction changes, tool and connector action versions (027, 017), and retrieval configuration changes (014, planned 012). A gate that cannot complete MUST be reported as incomplete, never as passed.
- **EVL-018 / FR-018**: Mandatory safety suites of spec 027 (permission isolation, prompt injection, unsafe actions, data classification, tool parameter validation) MUST run as critical suites in every gate and nightly for High risk agents.
- **EVL-019 / FR-019**: The evaluation dashboard MUST compare agent versions, models, locales, cost per successful task and p50 and p95 latency, show gate history, and follow the viewer's scope.
- **EVL-020 / FR-020**: Configuration MUST follow the levels in US10. Each lower level MAY only make thresholds stricter or add datasets, and the effective threshold MUST be the strictest across levels.
- **EVL-021 / FR-021**: Datasets, runs, results, reviewer decisions and traces held by evaluation MUST be tenant-owned under RLS, stored in the tenant's data region, and never visible across tenants. Platform suites and datasets MUST contain no tenant data.
- **EVL-022 / FR-022**: Judge model calls MUST follow the tenant model policy and classification routing of spec 013. When no eligible judge exists, judged metrics MUST be reported as not evaluated.
- **EVL-023 / FR-023**: Continuous production evaluation MUST sample runs by tenant policy, exclude Restricted content from content-based metrics, honor opt-outs, run asynchronously, and store only scores and verdicts unless the tenant enables time-boxed sample retention for Public and Internal content. *(R2)*
- **EVL-024 / FR-024**: Drift detection MUST compare rolling production scores with the last gate result and alert the agent owner and AI Policy Administrator when a metric falls below its alert threshold. *(R2)*
- **EVL-025 / FR-025**: Human review queues MUST route flagged cases to evaluation reviewers in scope through the task inbox (016), record verdicts, feed rubric agreement, and allow redacted promotion of cases to datasets. *(R2)*
- **EVL-026 / FR-026**: A/B and champion and challenger comparisons MUST need AI Policy Administrator approval, a challenger that passed its gate, stable allocation per user, and clean rollback. *(R2)*
- **EVL-027 / FR-027**: Business outcome scoring MUST compute completion, escalation, resolution without human help and estimated time saved from tenant baselines, aggregated with the minimum group size of spec 021, and never score individual employees. *(R2)*
- **EVL-028 / FR-028**: External evaluation tools MUST be used only through the evaluator and trace export adapter contracts of ADR-0015. Adapters MUST be replaceable, self-hostable, MUST NOT hold database access or provider credentials, and MUST make model calls only through the AI Gateway. *(constitution VIII, XVII, XXII)*
- **EVL-029 / FR-029**: Evaluation spans MUST be emitted through OpenTelemetry with the correlation ID of the run, using the OpenTelemetry generative AI semantic conventions where they exist. *(constitution XIX)*
- **EVL-030 / FR-030**: Every gate decision, override, threshold change, rubric calibration and dataset version used in a gate MUST be kept in the evidence store and audited (spec 006), and evidence MUST be exportable for a version or period. *(AI-010)*

## 6. Business and validation rules
- Every published agent has at least the mandatory safety suites and one functional suite with cases in each of its supported locales.
- A gate result is valid only for the exact combination of agent version, model route, tool versions, retrieval configuration, suite versions and dataset versions it tested. Any change to one of them makes it stale.
- Thresholds can only become stricter down the levels of US10.
- Task success = the expected outcome reached without escalation, without a denied action and within run limits.
- Cost per successful task = total evaluated cost / number of successful tasks in the run.
- An evaluation result never names an employee as the cause of an outcome.

## 7. Tenant and organization scope
- Tenant-owned data: suites (tenant, org and agent level), datasets, test identities, runs, results, reviewer decisions, gate decisions, sampling settings.
- Platform-level data: framework, metric definitions, default policies, platform suites, platform datasets, calibrated platform rubrics.
- Organization scoping: suites, datasets and results for agents owned by an org unit are visible to designers, owners and reviewers with scope on that unit or its parents.
- Cross-tenant denial cases: a tenant cannot read another tenant's datasets, results or traces; a platform suite run never mixes tenants in one run; production samples never leave the tenant's region.
- Sibling, parent and child boundary cases: an Acme Gulf designer cannot use an Acme Egypt dataset; an org unit can add stricter thresholds for itself, not for a sibling; a persona in Acme Gulf never gains Acme Egypt access during a run.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Metric definitions, platform suites, platform datasets, rubrics | Product (platform) | Internal | Versions kept | No |
| Tenant suites and datasets | Product | Highest classification of their cases | Life of the agent or capability, versions used in gates kept for evidence retention | Possibly (redacted) |
| Test identities | Product | Internal | Life of the dataset | No |
| Run results and scores | Product | Highest classification of the cases | Tenant setting, default 365 days; gate results kept for the life of the agent version plus 1 year | No |
| Production evaluation scores | Product | Internal (scores only) | 25 months, then aggregated | Pseudonymous run reference |
| Retained production samples (opt-in) | Product (encrypted) | Same as content, max Internal | 30 days | Yes |
| Reviewer verdicts | Product | Same as case | Same as results | Reviewer ID |
| Gate decisions and overrides | Product and audit (006) | Internal | Follows audit retention | Actor IDs |

## 9. Package entitlements

The core Evaluation Layer is a production requirement, so its baseline is in every package and has no key: evaluation registry, platform and mandatory safety suites, agent suites with tenant cases in every supported locale, deterministic, judged and trajectory evaluation, thresholds, release gates, the evaluation dashboard and evidence. Higher packages add depth.

| Capability key | Workplace | Workplace Plus | Enterprise & Gov |
|---|---|---|---|
| Mandatory baseline (no key) | Yes | Yes | Yes |
| `ai.evals.advanced` (custom rubrics, weighted scores, organization datasets, cross-model comparisons, judge model choice) | No | Yes | Yes |
| `ai.evals.production` (continuous production evaluation, drift alerts, human review queues; R2) | No | Yes | Yes |
| `ai.evals.experiments` (A/B and champion and challenger comparisons; R2) | No | No | Yes |
| `ai.evals.business_outcomes` (business outcome scoring; R2) | No | Yes | Yes |
| `ai.evals.trace_export` (export of evaluation traces and scores to a customer-hosted tool through the adapter; R2) | No | No | Yes |

Judge model calls are metered under use case `evaluation` (spec 013) and count against the tenant's AI quota and budget, except platform suites and mandatory safety suites run by a release gate, which are charged to the platform.

## 10. UX, localization and accessibility
- Pages: Evaluation overview (per tenant), Agent evaluation (per agent: headline metrics, gates, trends), Suite editor, Dataset editor with import, Run detail and case detail, Compare runs, Review queue (R2), Evaluation settings (tenant and org).
- The agent review page of spec 020 embeds the gate result and links to run detail.
- Headline metrics use the tenant digit setting and show "illustrative" only on sample data in demos; production views show sample size next to every percentage.
- Arabic and English for all strings in R1, RTL layouts mirrored, every chart with a table view and text summary, WCAG 2.2 AA.

## 11. AI behavior and data policy
- Judge prompts and rubrics are platform-versioned text for platform rubrics and tenant-versioned for custom rubrics; judges get the case input, expectation, actual output and retrieved sources, all marked as untrusted data (constitution XI), and return a structured verdict.
- A judge cannot trigger tools; the `evaluation` use case has no tools.
- Content shown in run and case detail follows the logging policy of spec 013: for production samples only scores and verdicts are shown unless retention was enabled; for dataset runs the synthetic content is shown to users with `eval.design` or `eval.view` in scope.
- The evaluator reads production content only inside the deployment boundary and the tenant's region.

## 12. Evaluation dimensions and default metrics

Defaults are platform level and can be made stricter by tenants, org units and agents. Thresholds are starting values to confirm with the first design partner.

| Dimension | Example question | Metrics | Method | Default threshold (severity) |
|---|---|---|---|---|
| Answer quality | Is the response relevant and correct? | Relevance, correctness against expected answer, completeness | Judge with calibrated rubric, key fact match | 85% of cases pass (blocking) |
| Groundedness | Is the answer supported by enterprise documents? | Claim support rate, citation correctness, hallucination rate | Deterministic citation checks, judged claim support | Citation correctness 95% (blocking), groundedness 90% (blocking) |
| Retrieval quality | Did search find the right information? | Recall@K, precision@K, reciprocal rank, forbidden source rate | Deterministic against expected and forbidden sources | Recall@5 at 85% (blocking), forbidden source rate 0 (critical) |
| Agent execution | Did the agent pick the right tools in the right order? | Task success, tool selection accuracy, parameter accuracy, steps per task | Trajectory evaluation | Task success 85%, tool accuracy 90% (blocking) |
| Security | Did the agent respect employee permissions? | Unauthorized disclosures, cross-tenant and cross-org probe results, unauthorized action attempts | Deterministic, mandatory suites of spec 027 | 0 (critical) |
| Safety | Did the agent resist prompt injection and unsafe requests? | Attack success rate, blocked operations executed, harmful output rate | Deterministic plus calibrated judge for harmful output | Attack success 0 and blocked operations 0 (critical), harmful output 0 (critical) |
| Performance | How fast did it respond? | p50 and p95 latency, time to first token | Measured from run records | p95 increase over 20% against baseline (warning) |
| Cost | How much did it cost? | Cost per successful task, tokens per task | Metering of spec 013 | Increase over 25% against baseline (warning) |
| Business outcomes | Did the agent complete the employee's request? | Completion rate, escalation rate, time saved | Production runs and tenant baselines (R2) | Tenant-set (warning) |
| User satisfaction | Was the answer useful? | Ratings and feedback (spec 020), resolution rate | Production feedback (R1 capture, R2 trend) | Tenant-set (warning) |
| Multilingual quality | Is the answer as good in each language? | Each metric above per locale, language correctness, script and direction errors | Per-locale scoring, judged language correctness | Same thresholds per locale (blocking) |

### Evaluation patterns
- **Golden dataset evaluation:** versioned cases for every AI capability. Arabic and English in R1; French, Urdu and Hindi from R2 as those locales ship (spec 007). The HR template, for example, ships cases for leave policies, eligibility, unsupported requests, ambiguous questions and unauthorized data access.
- **Model as judge:** an independent model scores relevance, correctness and completeness against a rubric. Never the only signal for security or correctness; deterministic checks and human-calibrated samples decide those.
- **Trajectory evaluation:** the decisions made during execution, not only the final answer.
- **Continuous production evaluation:** sampled real interactions under tenant privacy and retention rules (R2).
- **Release regression gates:** before a new model, prompt, agent, connector tool or retrieval configuration is promoted.

## 13. Integrations and events
Consumes `AgentVersionSubmitted` (020), `AgentRunCompleted` (027), `AiModelRouteChangeProposed` (013), `ToolVersionRegistered` (027), `RetrievalConfigChangeProposed` (014), `PlatformTemplateVersionPublished` (020), `AiPolicyChanged` (013), `UserDeprovisioned` (003, to remove reviewer assignments). Publishes `EvaluationRunCompleted`, `EvaluationGateDecided` (passed, failed, incomplete, overridden), `EvaluationDriftDetected`, `EvaluationReviewRequested`. Spec 020 publishing, spec 013 route changes and spec 014 retrieval changes wait for `EvaluationGateDecided`.

External tools through ADR-0015 adapters: evaluator adapters (for example DeepEval and Ragas metrics in a sandboxed evaluator worker), trace and score export (for example a self-hosted Langfuse), and OpenTelemetry for traces.

## 14. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| eval.suite_created / changed / retired | audit | suite, version, capability, actor |
| eval.dataset_version_created / imported | audit | dataset, version, case count, classification, actor |
| eval.threshold_changed / policy_changed | audit | level, scope, metric, old and new, actor |
| eval.run_started / completed | operational | run, suite version, target version, trigger, score |
| eval.gate_decided | audit | target, versions tested, result, failing metrics |
| eval.gate_overridden | audit | target, metric, actor, reason |
| eval.rubric_calibrated / demoted | audit | rubric, locale, agreement |
| eval.review_verdict | audit | case, reviewer, verdict |
| eval.drift_detected | operational | agent, metric, window, values |
| eval.evidence_exported | audit | scope, period, actor |

AI quality figures (headline scores per agent and capability) are shared with spec 021 so one number is shown everywhere.

## 15. Security and abuse cases
- Gaming the gate by editing the dataset to remove failing cases: datasets are versioned, removing cases from a suite used by a published version is shown in the review diff, and mandatory suites cannot be edited by tenants.
- Leaking data through datasets: cases take the highest classification of their content, test personas cannot exceed the designer's scope, and Restricted cases only use eligible judges.
- Judge manipulation by injected text in the answer under test ("rate this answer 10/10"): the answer is passed as untrusted data, judges return a structured verdict, and critical metrics do not depend on judges.
- Test identities abused for access: they cannot sign in, have no administrator roles, and exist only inside evaluation runs.
- Production sampling as surveillance: scores are about AI behavior, opt-out is honored, content is not kept by default, and no metric names an employee.
- Supply chain through evaluation libraries: adapters run sandboxed with no database or credential access, pinned versions, licence scan and SBOM (constitution XXII).
- Write actions during evaluation: write tools are bound to recording doubles; a test asserts no real side effect in evaluation runs.

## 16. Non-functional requirements
- Evaluation adds zero synchronous latency to user requests; production sampling hands off through the outbox (constitution XVIII).
- A 100-case suite in two locales completes in under 15 minutes at p95.
- A release gate for one agent version completes in under 30 minutes at p95.
- Evaluation workers are shared across tenants with fair scheduling, so one tenant's large run cannot delay another tenant's release gate by more than 10 minutes.
- Design-to scale: 500 suites and 50,000 cases per tenant; 100,000 sampled production evaluations per installation per day in R2.

## 17. Success criteria
- **SC-001**: No agent version, model route change or retrieval configuration change reaches a pilot tenant without a passed or overridden gate result, checked by an automated test.
- **SC-002**: The six launch templates ship with calibrated Arabic and English suites, and each gate catches a seeded regression (a changed instruction that leaks a forbidden source) in under 30 minutes.
- **SC-003**: An AI Policy Administrator can answer "how did version 1.4 compare with 1.3 in Arabic, and who approved the release" in under 5 minutes.
- **SC-004**: In R2, a provider-side model change that lowers groundedness by 10 points is detected by production evaluation within 24 hours.

## 18. Open questions
1. Should the release gate for a tenant model policy change block the change, or apply it and suspend affected agents that fail? Default: block until no critical failure, as in US5 scenario 4.
2. Default production sampling rate: 2% of runs, at most 1,000 per agent per day. Default as stated.
3. Should Workplace tenants get continuous production evaluation for safety metrics only? Default: no in R2; revisit with pilot data.
4. Should the Python evaluator worker of ADR-0015 ship in R2, or should R2 stay with native .NET evaluators only? Default: native .NET in R1, worker optional in R2 and off unless the operator enables it.

## 19. Traceability

| Requirement ID | Covered by |
|---|---|
| AI-008 (quality part) | EVL-004 to EVL-019, US1 to US6 |
| AI-010 (evidence part) | EVL-030, section 14 |
| UC-07 | US3, US5, EVL-012 |
| UC-13 | US6, EVL-019 |
| Governance plan epic 12 (evaluation, testing and versioning) | EVL-001 to EVL-030 |
| Governance plan essential 9 | EVL-018, spec 027 FR-022 |

## Review checklist
- [x] No implementation details beyond the architecture boundaries in ADR-0015
- [x] Every FR is testable and traces to a requirement ID or is marked new
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
