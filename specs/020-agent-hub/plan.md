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

## R2 plan
**Date:** 2026-11-14. R2 adds US4 (Agent Studio), US6 (marketplace), US8 scenarios 2 to 4 (briefings), US9 (memory), the department templates 7 to 12, detailed agent analytics through spec 021 (FR-018), and closes the R1 runtime gaps (T004, T005). Handoffs (US10) and the advanced templates are R3.

R2 slices, in order:
1. **Studio (US4, FR-019):** `POST /api/v1/agents` with no template creates a blank draft, for holders of `agent.design` when `ai.agents.custom` is on and the tenant is under `ai.agents.max_custom` (customer-created agents are those with no template). Over the limit, `conflict.agents.custom_limit` names the limit and that the tenant administrator can raise it through the package. `POST /api/v1/agents/{id}/clone` creates a draft with a new identity and no published history; audience, tools and memory are copied only when the request names each (`copyAudience`, `copyTools`, `copyMemory`). The tool picker (`GET /api/v1/agents/tools?agentId=`) offers only registry tools allowed by tenant policy and the package, with side effects and approval rule, and refuses at save any tool the designer's scope does not reach. The Studio page groups the definition in the areas of section 12; an administrator gets a read-only JSON view.
2. **Marketplace (US6, FR-010):** `GET /api/v1/agents/marketplace?q=&category=` lists published agents the person may use and those whose audience mode is `request_access`, grouped by category and department, in their language; `hidden` agents outside the audience never appear. `POST .../{id}/access-requests` creates an inbox task for the owner (task source `agent_access`) and the decision is shown to the requester and audited (`agent.access_requested`, `granted`, `denied`); a grant adds the person to the audience. Favourites (`PUT/DELETE .../{id}/favourite`) feed the assistant's agent list and the home shortcut strip (spec 008) through the existing contract. Ratings 1 to 5 with an optional comment (`PUT .../{id}/rating`): the average is shown to users, comments only to the owner, backup owner, AI Policy Administrator and the author (open question 3 default). Ratings are kept 2 years.
3. **Memory (US9, FR-015):** the tenant policy switch `persistentMemory` becomes settable. A definition's memory area is `session` (default) or `persistent` with retention days (default 90 unused). The person accepts memory per agent before anything is stored. A built-in tool `memory.remember` stores a short item with its classification; Restricted is refused, Confidential needs the agent's ceiling. Items are read into the context as the person's own notes, viewable, editable and deletable on "My agents"; deleted after retention of no use, on leaver (spec 003 event), when the person withdraws acceptance, and when the tenant turns the switch off. Deletion is audited as `agent.memory_deleted`.
4. **Briefings (US8 scenarios 2 to 4, FR-014):** the policy switch `scheduledAgents` becomes settable, with the risk classes allowed. An agent whose definition allows briefings offers "Daily briefing at a time" to its users; `PUT .../{id}/briefing {time, days}` subscribes. A worker runs due briefings as the subscribing user (spec 027 AGV-003) through the normal runtime and delivers the answer as a notification (spec 015, category `agent_briefing`). No run when the person is disabled, opted out, the agent is suspended or retired, or policy no longer allows it.
5. **Department templates (7 to 12):** Onboarding, People Directory and Expertise, Learning and Development, Procurement, Finance and Expenses, Internal Communications, behind `ai.agents.department`, with Arabic and English instructions and platform suites. Their tools are the ones the modules already register; a template whose autonomy needs L3 (Procurement, Internal Communications publishing) is "not ready" until spec 027 R2 builds L3.
6. **Runtime gaps (T004, T005):** answers stream over server-sent events from `POST .../runs` (as the assistant already does), and a run interrupted by a restart is resumed from its last checkpoint by a worker instead of being closed.
7. **Analytics (FR-018 R2):** the detailed agent analytics (per version: task success, escalations, tool success and failure, ratings, quality scores and cost) are offered through spec 021 when `analytics.advanced` is on.

### R2 data model (schema `agents`, forced RLS)
- `agent`: `audience_mode` (`open`, `request_access`, `hidden`), `cloned_from`.
- `agent_favourite (user_id, agent_id)`, `agent_rating (user_id, agent_id, score, comment, updated_at)`, `agent_access_request (id, agent_id, user_id, status, decided_by, decided_at)`.
- `agent_memory_consent (user_id, agent_id, accepted_at)`, `agent_memory (id, agent_id, user_id, text, classification, created_at, last_used_at)`.
- `agent_briefing (user_id, agent_id, time, days, time_zone, next_run_at, last_run_at)`.

### R2 tests
- Studio: blank create needs the capability and the limit; clone copies only what was confirmed; tool picker and save refuse tools outside policy or scope.
- Marketplace: hidden agents never listed; request access creates the owner's task and the outcome reaches the requester; favourites and ratings per user; comments private; cross-tenant not found.
- Memory: nothing stored without both switches and the person's acceptance; Restricted refused; view, edit, delete; retention, leaver and withdrawal delete; another user never sees an item.
- Briefings: not offered when policy forbids; runs as the user with current permissions; stops on opt-out, suspension or a disabled user.
- e2e: Studio, marketplace and My agents in English and Arabic with axe.

## R3 plan
**Date:** 2026-11-25. R3 adds handoffs between agents (US10, FR-016) and the advanced templates 13 to 15. Extensibility in the R3 release line is met by spec 027 R3 (MCP servers as tool sources) and the connector tools of spec 017; it needs no further work here.

R3 slices, in order:
1. **Handoffs (US10, FR-016, `ai.agents.orchestration`):** a definition declares up to 5 handoffs, each a target and a topic per language. In a platform template the target is a template ID, resolved to the tenant's published agent installed from it; a customer agent may also name an agent ID. The runtime offers the built-in tool `agent.handoff` (read side effect, parameters `to` from the declared targets and `request`), checked by the gateway like any tool. A handoff ends the current agent's turn and starts a run of the target agent as the same person, with the target's own definition, tools, policy and limits; the person sees which agent took over. Runs keep `parent_run_id` and `depth`; the first agent is depth 1, and at depth 3 the tool is refused (`tool.handoff_depth`) and the answer offers the agent's human contact. An undeclared target, a target the person may not use, or a target that is not published is refused (`tool.handoff_not_declared`, `tool.handoff_unavailable`). Every handoff is audited (`agent.handoff`).
2. **Advanced templates (13 to 15, `ai.agents.advanced`):** Legal and Compliance (L0: policies, documents and compliance evidence search), Executive Intelligence (L0: knowledge and the analytics tools that exist), Process Automation (L4, only as agent tasks of approved processes; "not ready" without `ai.agents.autonomous`), with Arabic and English instructions and platform suites.
3. **Pages:** handoffs in the agent editor (targets and topics) and the "handed over to" notice in the conversation; OpenAPI, R3 notes and evidence.

Defaults (spec silent):
- Package scope: `ai.agents.orchestration` (Workplace Plus and Enterprise and Government) allows handoffs declared by a platform template; `ai.agents.orchestration_custom` (Enterprise and Government) also allows handoffs a tenant adds or that name an agent ID. Two flags keep the entitlement check a plain capability check.
- A handoff is one step of the current run and counts as one tool call. The target's answer is its own run; the first run ends with outcome `handed_off`.
- The target run gets the request the first agent wrote, not the conversation, so nothing the person did not say to it moves between agents beyond that request.

### R3 data model (schema `agents`, forced RLS)
- `run`: `parent_run_id`, `depth` (1 to 3).

### R3 tests
- A declared handoff runs the target as the person with its own tools; an undeclared one is refused; depth 3 refuses and offers the human contact; permissions never add up across the two agents; the package scope and cross-tenant hold.
- The advanced templates validate under the default policy in Arabic and English; Process Automation is not ready without `ai.agents.autonomous`.

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
