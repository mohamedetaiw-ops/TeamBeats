# Implementation Plan: Agent Hub (Registry, Templates, Studio and Marketplace)

**Spec:** `specs/020-agent-hub/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0011 (runtime in the `Agents` module, no agent framework), ADR-0013, spec 013 plan, spec 027 plan, spec 034 plan, module map (`Agents`)

## Summary
R1 builds the agent registry, the definition schema with versions, the common runtime, the six launch templates of the roadmap (knowledge, HR, IT, content, requests, productivity) at autonomy L0 to L2, installing and adapting a template (User Story 1), review and publish (User Story 2), rollback, suspend and retire (User Story 3), the evaluation gate as the Hub shows it (User Story 5), routing from the assistant and contextual actions (User Story 7), and agent-prepared tasks in the inbox (User Story 8, R1 part). Agent Studio for new agents, customer-created agents, the marketplace, memory, briefings, department templates and handoffs are R2 or R3.

R1 slices, in order:
1. **Runtime:** the run loop (model turn, tool calls through the spec 027 gateway, confirmation checkpoints), streaming to the web, run state and checkpoints on PostgreSQL, workers resuming after restart.
2. **Registry and definitions:** agent, version, definition schema (identity, instructions, tone, output format, knowledge sources, tools, audience, languages, escalation text, autonomy, model use case), lifecycle states.
3. **Templates:** six platform templates with platform suites (spec 034) and the "not ready" check for missing connectors.
4. **Lifecycle:** install, edit within limits, test panel, submit with inbox task, review diff, gate result, publish with separation of duties, rollback, suspend, retire.
5. **Reach:** routing from the assistant (with planned spec 014) and contextual actions on policy pages and documents; prepared tasks in the inbox.
6. **Pages:** agent registry, template library, agent editor with test panel, review page, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | Agents, versions, runs and checkpoints tenant-owned with forced RLS; platform templates are platform data read by tenants |
| III authorization | Yes | `agent.design`, `agent.publish` through `IAuthorizationService`; the last editor cannot approve Medium or High risk versions |
| VIII, X AI | Yes | Models only through `IAiGateway`, tools only through the tool gateway; no write action without the user's confirmation (L2) |
| XXI entitlements | Yes | Template visibility by package; `ai.agents.basic`; customer-created agents refused in R1 |

## Data model (schema `agents`)
- `agent`: name and description per locale, icon, `template_id`, `owner_user_id`, `status` (draft, in_review, published, suspended, retired), `current_version`, `risk_class`.
- `agent_version`: `number`, `definition jsonb` (validated schema), `state`, `submitted_by`, `last_editor`, `approved_by`, `gate_result_id`, `published_at`.
- `agent_template` (platform, RLS exception): definition, default autonomy, required connectors, platform suite IDs, package capability.
- `run_checkpoint`: run state for resumption.

## Runtime
A small loop in `Agents`: build the prompt from the definition and the user's turn, call `IAiGateway.StreamChatAsync` with use case `agent` and the version's classification ceiling, pass tool calls to the gateway, append results inside the untrusted data envelope, stop on answer, confirmation, limit or suspension. Checkpoints after every step; a worker picks up a run after a restart. Answers stream to the browser over server-sent events through the API host.

## Launch templates (R1)
Knowledge (L0, search and documents), HR (L0 to L2, policies, HR services), IT (L2, IT services), Content (L1, drafts content as drafts only), Requests (L2, service catalogue drafts), Productivity (L0, my tasks and calendar). Each ships Arabic and English instructions and its platform suite. Sources that do not exist yet (for example search before spec 012) mark the template "not ready".

## Publishing and the gate (US2, US5)
Submit creates a gate request to spec 034 (`EvaluationGateRequested`) and an inbox task for publishers. Publish is refused while the gate reports a critical failure or an unoverridden blocking failure, and refused for the last editor of Medium and High risk versions. New conversations use the new version within 60 seconds; running ones finish on theirs. Rollback re-checks the older version against current policy.

## Reach (US7, US8)
The assistant (planned spec 014) asks `Agents` to route a question: candidates are agents the user may use, ranked by a routing model call through the gateway with the agents' descriptions only, and the choice is recorded. Contextual actions pass an item reference; the item is read with the user's permissions. Prepared drafts go to the inbox as tasks from the `Agents` task source.

## API (v1)
- `GET /api/v1/agents`, `GET /api/v1/agents/templates`, `POST /api/v1/agents/install {templateId}`, `GET/PUT /api/v1/agents/{id}/draft`, `POST .../submit`, `GET .../review`, `POST .../publish`, `POST .../rollback {version}`, `POST .../retire`.
- `POST /api/v1/agents/{id}/test` (test panel, no write actions), `POST /api/v1/agents/{id}/runs` (start a conversation turn, streamed), `POST /api/v1/agents/route`.

## Tests
- Integration per user story: US1 scenarios 1 to 5, US2 scenarios 1 to 5, US3 scenarios 1 to 3, US5 scenarios 1 to 4, US7 scenarios 1 to 4 (with the assistant), US8 scenario 1.
- Runtime: deterministic replies from the fake OpenRouter; worker restart resumes; suspension stops before the next tool call.
- Boundaries: agents, versions and runs of another tenant not found; template visibility by package.
- Acceptance (e2e): install the HR template, adapt it, test, submit, publish and use it, in English and Arabic with axe.
