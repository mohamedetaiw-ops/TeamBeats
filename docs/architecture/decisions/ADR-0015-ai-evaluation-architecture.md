# ADR-0015: AI evaluation as a bounded context with replaceable evaluator adapters

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-05 |
| Owners | Mohamed |
| Related specs | 034, 013, 014, 020, 021, 027 |
| Supersedes | |

Numbering note: ADR-0013 and ADR-0014 are taken by the architecture alignment work, so this record is 0015.

## Context and constraints
Spec 034 makes AI evaluation a core platform capability: versioned suites and golden datasets, deterministic, judged and trajectory evaluators, thresholds, release gates, a dashboard and, from R2, continuous evaluation of sampled production runs. It must work the same on shared SaaS, dedicated and air-gapped on-premises installs (constitution XX), keep every tenant's datasets and traces inside its isolation and residency boundary (constitution I, spec 026), and never send content to a model outside the AI Gateway (constitution VIII, IX).

Good open-source evaluation tools exist, mostly in Python: DeepEval (quality metrics and test runner, Apache-2.0), Ragas (retrieval and RAG metrics, Apache-2.0) and Langfuse (traces, prompt management and evaluation workflows, MIT core with separately licensed enterprise folders). ADR-0011 keeps the agent runtime in .NET and allows Python evaluation tooling offline in CI. Constitution XV and XXII require an ADR before adding infrastructure or dependencies.

## Decision drivers
- Evaluation must not slow or block user requests; inline safety checks already live in specs 013 and 027.
- Tenant data, judge calls and results must follow the same isolation, classification routing and residency rules as the rest of the product.
- The evaluation management capability (registry, datasets, thresholds, gates, evidence, dashboard) is product value and must be ours. Individual metric algorithms are commodity.
- Any external tool must be replaceable and self-hostable.

## Options considered
1. **Build everything in .NET, no external tools.** One language and full control. Slow to reach the breadth of RAG and judge metrics the tools already have, and we would maintain metric research ourselves.
2. **Adopt a Python evaluation stack as a separate service (DeepEval, Ragas, Langfuse) as the core.** Fast start, rich metrics. Puts datasets, results and traces in a second system with its own tenancy and permission model, adds a service and database to every on-premises install, and lets tools call model providers directly unless every one is wrapped. The evaluation evidence used for release decisions would live outside our audit chain.
3. **Use a hosted evaluation SaaS.** No operations work. Sends tenant content and traces outside the customer's boundary, fails residency and air-gapped installs.
4. **Evaluation module in the monolith, with adapter contracts for external evaluators and trace export.** The control plane, execution plane and evidence store are ours; metric engines can be ours or external behind a contract.

## Decision
Option 4.

- An `Evaluation` module in the modular monolith owns suites, datasets, rubrics, thresholds, policies, runs, results, gate decisions and evidence, in its own schema under RLS (ADR-0002), with jobs on the existing outbox and workers (ADR-0006). Other modules interact through its contracts and events (`EvaluationGateDecided`, `EvaluationRunCompleted`).
- Three internal layers: control plane (configuration, datasets, rubrics, thresholds, permissions, dashboards), execution plane (runners, evaluators, scoring, regression comparison, async processing), evidence store (versioned results, trace references, scores, reviewer decisions, audit through spec 006).
- Evaluation is never in the synchronous request path. Production runs reach it through `AgentRunCompleted` and outbox messages.
- R1 evaluators are native .NET: deterministic retrieval metrics, citation checks, trajectory matching on execution records (spec 027), and judged metrics through the AI Gateway under use case `evaluation`.
- An `IEvaluator` adapter contract takes a case, the actual output, retrieved sources and trajectory, and returns structured scores. From R2 an optional sandboxed evaluator worker image can host DeepEval and Ragas metrics behind that contract. The worker ships in the same release artifacts as everything else, is off unless the operator enables it, has no database access and no provider credentials, and reaches models only through an internal OpenAI-compatible facade of the AI Gateway with a short-lived token scoped to one tenant and the `evaluation` use case (spec 013 AIG-018). Content passed to it stays inside the deployment and the tenant's region.
- A trace and score export adapter can send evaluation spans and scores to a tool the customer or platform hosts, for example a self-hosted Langfuse, from R2 (`ai.evals.trace_export`). It is never the system of record and receives only what the tenant's logging policy allows. Langfuse enterprise-licensed features are not used.
- Traces use OpenTelemetry, following its generative AI semantic conventions where they exist, with the run's correlation ID (constitution XIX).

## Consequences and risks
- We build and own the registry, gates and evidence store; that is the product part and it keeps release decisions inside our audit chain.
- Metric breadth in R1 is limited to what we implement natively. The adapter lets us add DeepEval and Ragas metrics in R2 without changing the control plane.
- An optional Python worker adds a second language to the release artifacts. It is out of the request path and isolated, so it does not reopen ADR-0011. If it proves unnecessary, it is not built.
- Judged metrics vary between runs. Spec 034 requires calibration against human labels, noise margins and deterministic checks for critical thresholds.
- Licences of external tools are checked by the licence scan and SBOM on every release (constitution XXII); a change to a non-OSI licence removes the adapter.

## Security and multi-tenancy implications
- All evaluation data carries `tenant_id` under RLS. Workers set the tenant context before running a case; one run never mixes tenants.
- Cases run as synthetic test identities with the persona's permissions, never as administrators; write tools are bound to recording sandbox doubles.
- Judge calls follow tenant model policy and classification routing. Restricted content is never sent to a shared provider for judging.
- The evaluator worker and the export adapter hold no database access and no provider or connector credentials.

## Rollout and migration plan
R1: Evaluation module with registry, datasets, native evaluators, thresholds, release gates for agent versions, model routes and retrieval configuration, dashboard and evidence; platform suites for the six launch templates in Arabic and English. R2: production sampling, drift detection, review queues, comparisons, business outcomes, French, Urdu and Hindi datasets, optional evaluator worker, trace export adapter. R3: evaluation of L4 process runs end to end.
