# Implementation Plan: Agent Governance, Tools and Safety

**Spec:** `specs/027-agent-governance/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0011 (agent runtime in the `Agents` module), ADR-0013, spec 013 plan (AI Gateway), spec 034 plan (release gate), module map (`Agents`)

## Summary
R1 builds the governance layer that every agent run passes through: autonomy L0 to L2 (User Story 1), agents acting with the intersection of the user's permissions and the agent's grants (User Story 2), the tool registry and tool gateway for product tools (User Story 3), user confirmation for L2 actions (User Story 4, R1 part), untrusted content handling (User Story 5), execution records and search (User Story 6, R1 part), suspension, kill switch and rollback (User Story 7), the tenant agent policy (User Story 8, tenant part), and risk classes with the mandatory safety suites (User Story 9). L3 approvals, connector tools, org unit policies, the Security Center, incidents and anomaly alerts are R2.

The runtime itself (run loop, checkpoints, routing) is shared with spec 020 and lives in the same `Agents` module; this plan covers the policy and safety parts, and spec 020's plan covers definitions, templates and lifecycle.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation and org scope | Yes | Runs, records, policies and registry entries tenant-owned with forced RLS; workers set tenant context before resuming a run; org boundary through `IAuthorizationService` |
| III authorization | Yes | Every tool call checks the user's authorization at execution time (constitution VII), intersected with the agent's grants |
| VII, VIII, IX, XI AI safety | Yes | Models only through `IAiGateway`; tools only through the tool gateway; untrusted content wrapped; Restricted never to shared providers |
| XII degraded states | Yes | Kill switch stops agents within 10 seconds and leaves the intranet working |
| XXI entitlements | Yes | `ai.agents.basic` for L2; L3 and L4 keys refused in R1 |

## Tool registry and gateway
- `tool_definition` (platform and module tools, versioned): ID, owning module, parameter JSON schema, side effect (`read`, `draft`, `write_self`, `write_external`), default approval rule (US4 table), classification of outputs, idempotent flag.
- Modules register tools in code through `Agents.Contracts` (`IToolRegistration`), like component registrations; each tool handler calls the owning module's contract under the user's authorization context. R1 tools: search knowledge (planned spec 012), read policy clauses (025), read documents (011), list services and draft a request (016), read my tasks (016), read my calendar (018, T017 there), draft content (009).
- The gateway runs the checks of US3 scenario 1 in that order and writes one tool call entry to the execution record. Parameter errors return a short message to the model. Unknown tools are refused and recorded as policy events. Default limit 20 tool calls per run.
- L2 confirmation: a `write_self` tool call returns a confirmation card (parameters in plain language, sources of each value marked when they came from retrieved content, US5 scenario 4) and the run waits on a checkpoint. Confirm executes exactly the confirmed parameters, bound by a hash; the draft can also go to the inbox as "prepared by AI" (spec 020 US8).

## Untrusted content (US5)
Retrieved content and tool outputs are passed to the model inside a data envelope with a fixed system rule; they can never produce a tool call by themselves (a tool call needs a model turn, which the gateway checks like any other). A detector flags instruction-like patterns and records `agent.injection_detected` with the source item. The web renderer for agent answers loads no external images and shows links outside the tenant allowlist as text.

## Policy (US8, tenant part) and risk classes (US9)
- `agent_policy` per tenant, versioned: maximum autonomy, allowed tools, stricter approval rules, maximum classification, scheduled agents allowed, memory allowed, run limits. Cache with 60 second effect; on save, published agents that break it are suspended and their owners told.
- Risk class computed from the definition on every save (table in US9), never lowered by hand.

## Execution records (US6, R1 part)
`agent_run` and `agent_run_step`: agent and version, user, acting unit, routing decision, model and provider (from the gateway result), retrieved source IDs, tool calls with masked Confidential and Restricted values, policy decisions, confirmations, outcome, duration, tokens and cost. Text follows the spec 013 logging policy (metadata only by default). Search by agent, user, tool, outcome, denial and time for security officers in scope.

## Emergency controls (US7)
- Suspend an agent: new runs refused, running runs stop before their next tool call (checked in the gateway), pending confirmations cancelled.
- Tenant kill switch: a flag in the policy cache, checked on every run step and tool call; cache refresh for this flag is pushed through the outbox and polled every 5 seconds, so all agents stop within 10 seconds.
- Platform global switch for a template or tool, notifying tenant administrators.

## Mandatory safety suites (US9 scenario 2)
Platform suites shipped with spec 034: permission isolation, prompt injection (constitution fixtures), unsafe actions, data classification, tool parameter validation. Critical and 100% pass on every agent version gate.

## API (v1)
- `GET/PUT /api/v1/agents/policy`, `POST /api/v1/agents/kill-switch`, `POST /api/v1/agents/{id}/suspend`, `.../resume`.
- `GET /api/v1/agents/runs?agent=&user=&tool=&outcome=`, `GET /api/v1/agents/runs/{id}`.
- `POST /api/v1/agents/runs/{id}/confirmations/{cid}` (confirm or cancel).
- `GET /api/v1/agents/tools` (registry view).

## Tests
- Integration per user story: US1 scenarios 1, 2 and 4; US2 scenarios 1 to 4; US3 scenarios 1 to 5; US4 scenarios 4 and 5 for confirmation; US5 scenarios 1 to 4; US6 scenarios 1 to 3; US7 scenarios 1, 2, 5 and 6; US8 scenarios 1, 2 and 4; US9 scenarios 1 to 3.
- Kill tests: worker restart mid-run resumes from the checkpoint; kill switch within 10 seconds under load.
- Cross-tenant and cross-org probes as part of the permission isolation suite.
- Acceptance (e2e): confirmation card in English and Arabic with axe; security officer run search.
